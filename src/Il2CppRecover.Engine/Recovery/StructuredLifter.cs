namespace Il2CppRecover.Recovery;

/// <summary>
/// Lifts an acyclic AArch64 function into structured statements. Values are tracked per register
/// and stack slot. Branches are structured with immediate post-dominators. A loop is reported
/// so the caller can use the legacy single-while matcher. An instruction outside this subset
/// stops the lift with a diagnostic that names it.
/// </summary>
public static class StructuredLifter
{
    public sealed class Outcome
    {
        public List<Stmt>? Statements { get; init; }
        public List<string> Diagnostics { get; } = [];
        public List<string> Evidence { get; } = [];
        public bool HasLoop { get; init; }
        public bool CandidateOnly { get; set; }
        public int BlockCount { get; init; }
    }

    public static Outcome Lift(RecoveryRequest request, DiscoveredFunction fn)
    {
        if (fn.Issues.Count > 0)
        {
            var blocked = new Outcome();
            blocked.Diagnostics.AddRange(fn.Issues);
            return blocked;
        }
        var blocks = BuildBlocks(fn, out var blockAt);
        if (HasCycle(blocks, blockAt, request))
            return new Outcome { HasLoop = true, BlockCount = blocks.Count };
        if (blocks.Count > request.Budget.MaxBlocks)
        {
            var limited = new Outcome { BlockCount = blocks.Count };
            limited.Diagnostics.Add($"AnalysisBudgetExceeded: {blocks.Count} basic blocks exceeds the limit of {request.Budget.MaxBlocks}.");
            return limited;
        }
        var ctx = new Ctx(request, fn, blocks, blockAt);
        try
        {
            ctx.ComputePostDominators();
            var body = ctx.EmitRegion(blockAt[fn.Entry], null, ctx.InitialState(), out var fallsOff);
            if (fallsOff is not null)
                throw new LiftException("A path reaches the end of the structured region without a return.");
            Cleanup.Run(body, ctx.Check);
            var ok = new Outcome { Statements = body, CandidateOnly = ctx.CandidateOnly, BlockCount = blocks.Count };
            ok.Diagnostics.AddRange(ctx.Notes);
            ok.Evidence.AddRange(ctx.EvidenceNotes);
            return ok;
        }
        catch (LiftException ex)
        {
            var failed = new Outcome { BlockCount = blocks.Count };
            failed.Diagnostics.Add(ex.Message);
            failed.Evidence.AddRange(ctx.EvidenceNotes);
            return failed;
        }
    }

    internal enum TermKind { Ret, Jump, Cond, TailCall, NoReturn, Trap, Fall }

    internal sealed class Block
    {
        public ulong Start;
        public List<A64Op> Body = [];
        public A64Op? Term;
        public TermKind Kind;
        public ulong Next;
        public ulong Taken;
        public List<ulong> Succ = [];
    }

    private static List<Block> BuildBlocks(DiscoveredFunction fn, out Dictionary<ulong, Block> blockAt)
    {
        var leaders = new SortedSet<ulong> { fn.Entry };
        foreach (var op in fn.Instructions.Values)
        {
            if (op.IsConditionalBranch)
            {
                leaders.Add(op.Target);
                leaders.Add(op.Address + 4);
            }
            else if (op.Kind == A64Kind.B && !fn.TailCalls.Contains(op.Address) && fn.Instructions.ContainsKey(op.Target))
                leaders.Add(op.Target);
        }
        var blocks = new List<Block>();
        blockAt = [];
        foreach (var start in leaders)
        {
            if (!fn.Instructions.ContainsKey(start))
                continue;
            var block = new Block { Start = start };
            var address = start;
            while (fn.Instructions.TryGetValue(address, out var op))
            {
                if (address != start && leaders.Contains(address))
                {
                    block.Kind = TermKind.Fall;
                    block.Next = address;
                    break;
                }
                if (op.Kind == A64Kind.Bl && fn.NoReturnCalls.ContainsKey(address))
                {
                    block.Term = op;
                    block.Kind = TermKind.NoReturn;
                    break;
                }
                if (op.Kind is A64Kind.Ret) { block.Term = op; block.Kind = TermKind.Ret; break; }
                if (op.Kind is A64Kind.Trap) { block.Term = op; block.Kind = TermKind.Trap; break; }
                if (op.Kind == A64Kind.B)
                {
                    block.Term = op;
                    if (fn.TailCalls.Contains(address))
                        block.Kind = TermKind.TailCall;
                    else
                    {
                        block.Kind = TermKind.Jump;
                        block.Next = op.Target;
                    }
                    break;
                }
                if (op.IsConditionalBranch)
                {
                    block.Term = op;
                    block.Kind = TermKind.Cond;
                    block.Taken = op.Target;
                    block.Next = address + 4;
                    break;
                }
                block.Body.Add(op);
                address += 4;
            }
            if (block.Term is null && block.Kind != TermKind.Fall)
                block.Kind = TermKind.Trap;
            if (block.Kind is TermKind.Jump or TermKind.Fall)
                block.Succ.Add(block.Next);
            if (block.Kind == TermKind.Cond)
            {
                block.Succ.Add(block.Taken);
                block.Succ.Add(block.Next);
            }
            blocks.Add(block);
            blockAt[start] = block;
        }
        return blocks;
    }

    private static bool HasCycle(List<Block> blocks, Dictionary<ulong, Block> blockAt, RecoveryRequest request)
    {
        if (blocks.Count == 0)
            return false;
        var color = new Dictionary<ulong, int>();
        var stack = new Stack<(ulong Addr, bool Exit)>();
        stack.Push((blocks[0].Start, false));
        var steps = 0;
        while (stack.Count > 0)
        {
            if ((++steps & 255) == 0)
                CheckRequest(request);
            var (address, exit) = stack.Pop();
            if (!blockAt.ContainsKey(address))
                continue;
            if (exit)
            {
                color[address] = 2;
                continue;
            }
            if (color.TryGetValue(address, out var seen))
            {
                if (seen == 1)
                    return true;
                continue;
            }
            color[address] = 1;
            stack.Push((address, true));
            var succs = blockAt[address].Succ;
            for (var i = succs.Count - 1; i >= 0; i--)
                stack.Push((succs[i], false));
        }
        return false;
    }

    private static void CheckRequest(RecoveryRequest request)
    {
        request.CancellationToken.ThrowIfCancellationRequested();
        if (request.DeadlineTimestamp is long deadline && System.Diagnostics.Stopwatch.GetTimestamp() >= deadline)
            throw new AnalysisLimitException(MethodRecoveryStatus.TimedOut, "TimedOut: this method exceeded its analysis deadline before a body was recovered.");
    }

    internal sealed class Slot
    {
        public required Expr Value;
        public int Width;
    }

    internal enum FlagKind { Compare, FloatCompare, Test, AddCompare }

    internal sealed record Flags(FlagKind Kind, Expr Left, Expr Right, int Width);

    internal sealed class State
    {
        public Slot?[] X = new Slot?[31];
        public Slot?[] V = new Slot?[32];
        public long Sp;
        public Dictionary<long, Slot> Stack = [];
        public Flags? Flags;
        public State Clone()
        {
            var c = new State { Sp = Sp, Flags = Flags };
            Array.Copy(X, c.X, X.Length);
            Array.Copy(V, c.V, V.Length);
            c.Stack = new Dictionary<long, Slot>(Stack);
            return c;
        }
    }

    internal sealed class LiftException(string message) : Exception(message);

    private sealed class Ctx
    {
        private readonly RecoveryRequest _req;
        private readonly DiscoveredFunction _fn;
        private readonly List<Block> _blocks;
        private readonly Dictionary<ulong, Block> _blockAt;
        private readonly Dictionary<ulong, ulong?> _ipdom = [];
        private int _temp;
        private int _emitted;
        private long _nextPulse;
        private readonly string _selfType;
        public readonly List<string> Notes = [];
        public readonly List<string> EvidenceNotes = [];
        public bool CandidateOnly;

        public Ctx(RecoveryRequest req, DiscoveredFunction fn, List<Block> blocks, Dictionary<ulong, Block> blockAt)
        {
            _req = req;
            _fn = fn;
            _blocks = blocks;
            _blockAt = blockAt;
            _selfType = Qualified(req.Contract.Namespace, req.Contract.DeclaringType);
        }

        private static string Qualified(string ns, string type) => string.IsNullOrEmpty(ns) ? type : ns + "." + type;

        public void Check()
        {
            CheckRequest(_req);
            if (_req.Pulse is null)
                return;
            var now = System.Diagnostics.Stopwatch.GetTimestamp();
            if (now < _nextPulse)
                return;
            _nextPulse = now + System.Diagnostics.Stopwatch.Frequency / 4;
            _req.Pulse.Invoke();
        }

        public void ComputePostDominators()
        {
            var order = new List<ulong>();
            var seen = new HashSet<ulong>();
            var stack = new Stack<(ulong Addr, bool Exit)>();
            stack.Push((_fn.Entry, false));
            var steps = 0;
            while (stack.Count > 0)
            {
                if ((++steps & 255) == 0)
                    Check();
                var (address, exit) = stack.Pop();
                if (!_blockAt.ContainsKey(address))
                    continue;
                if (exit)
                {
                    order.Add(address);
                    continue;
                }
                if (!seen.Add(address))
                    continue;
                stack.Push((address, true));
                var succs = _blockAt[address].Succ;
                for (var i = succs.Count - 1; i >= 0; i--)
                    stack.Push((succs[i], false));
            }
            _postorder = new Dictionary<ulong, int>(order.Count);
            for (var i = 0; i < order.Count; i++)
                _postorder[order[i]] = i;
            foreach (var address in order)
            {
                if ((++steps & 255) == 0)
                    Check();
                var succs = new List<ulong>();
                foreach (var successor in _blockAt[address].Succ)
                {
                    if (_blockAt.ContainsKey(successor))
                        succs.Add(successor);
                }
                if (succs.Count == 0)
                {
                    _ipdom[address] = null;
                    continue;
                }
                ulong? immediate = succs[0];
                for (var i = 1; i < succs.Count; i++)
                    immediate = Intersect(immediate, succs[i]);
                _ipdom[address] = immediate;
            }
        }

        private Dictionary<ulong, int> _postorder = [];

        private ulong? Intersect(ulong? left, ulong? right)
        {
            var guard = 0;
            var limit = _postorder.Count + 2;
            while (left != right)
            {
                if (left is null || right is null || ++guard > limit)
                    return null;
                if (_postorder[left.Value] > _postorder[right.Value])
                    left = Parent(left.Value);
                else
                    right = Parent(right.Value);
            }
            return left;
        }

        private ulong? Parent(ulong address) => _ipdom.TryGetValue(address, out var parent) ? parent : null;

        public State InitialState()
        {
            var st = new State();
            var ngrn = 0;
            if (!_req.Contract.IsStatic)
            {
                st.X[0] = new Slot { Value = new ThisExpr { RefType = _selfType }, Width = 64 };
                ngrn = 1;
            }
            var nsrn = 0;
            foreach (var p in _req.Contract.Parameters)
            {
                var expr = new ParamExpr(p.Name, p.Type) { RefType = p.Type == PrimitiveKind.Reference ? p.DeclaredType : null };
                if (p.Type is PrimitiveKind.Single or PrimitiveKind.Double)
                {
                    if (nsrn >= 8)
                        throw new LiftException("More than eight floating-point parameters: stack-passed arguments are not lifted.");
                    st.V[nsrn++] = new Slot { Value = expr, Width = p.Type == PrimitiveKind.Double ? 64 : 32 };
                }
                else
                {
                    if (ngrn >= 8)
                        throw new LiftException("More than eight integer or reference parameters: stack-passed arguments are not lifted.");
                    st.X[ngrn++] = new Slot { Value = expr, Width = WidthOf(p.Type) };
                }
            }
            if (ngrn < 8)
                st.X[ngrn] = new Slot { Value = new OpaqueExpr("hidden MethodInfo* argument"), Width = 64 };
            for (var r = 19; r <= 28; r++)
                st.X[r] = new Slot { Value = new OpaqueExpr("caller x" + r), Width = 64 };
            st.X[29] = new Slot { Value = new OpaqueExpr("caller frame pointer"), Width = 64 };
            st.X[30] = new Slot { Value = new OpaqueExpr("return address"), Width = 64 };
            for (var r = 8; r <= 15; r++)
                st.V[r] = new Slot { Value = new OpaqueExpr("caller d" + r), Width = 64 };
            return st;
        }

        private static int WidthOf(PrimitiveKind k) => k is PrimitiveKind.Int64 or PrimitiveKind.UInt64 or PrimitiveKind.Reference ? 64 : 32;

        private enum RegionPhase { Emit, Join }

        private sealed class RegionFrame
        {
            public required Block Block;
            public ulong? Stop;
            public required State State;
            public List<Stmt> Stmts = [];
            public State? FallsOff;
            public RegionPhase Phase;
            public int Arm;
            public Expr? Condition;
            public ulong? Merge;
            public ulong TermAddress;
            public RegionFrame? Taken;
            public RegionFrame? Fall;
        }

        public List<Stmt> EmitRegion(Block start, ulong? stop, State st, out State? fallsOff)
        {
            var root = new RegionFrame { Block = start, Stop = stop, State = st };
            var stack = new Stack<RegionFrame>();
            stack.Push(root);
            var guard = _blocks.Count + 8;
            while (stack.Count > 0)
            {
                if (stack.Count > guard)
                    throw new LiftException("AnalysisBudgetExceeded: structuring nesting exceeded the number of basic blocks.");
                var frame = stack.Peek();
                if (frame.Phase == RegionPhase.Join)
                {
                    if (frame.Arm == 0)
                    {
                        frame.Arm = 1;
                        stack.Push(frame.Fall!);
                        continue;
                    }
                    if (FinishCond(frame))
                        stack.Pop();
                    else
                        frame.Phase = RegionPhase.Emit;
                    continue;
                }
                if (frame.Stop is ulong stopAt && frame.Block.Start == stopAt)
                {
                    frame.FallsOff = frame.State;
                    stack.Pop();
                    continue;
                }
                Check();
                if (++_emitted > _blocks.Count * 8 + 64)
                    throw new LiftException("Structuring would duplicate too much shared code; this control-flow graph is not structured.");
                var block = frame.Block;
                foreach (var op in block.Body)
                    LiftOne(op, frame.State, frame.Stmts);
                switch (block.Kind)
                {
                    case TermKind.Ret:
                        EmitReturn(block.Term!, frame.State, frame.Stmts);
                        frame.FallsOff = null;
                        stack.Pop();
                        break;
                    case TermKind.TailCall:
                        EmitTailCall(block.Term!, frame.State, frame.Stmts);
                        frame.FallsOff = null;
                        stack.Pop();
                        break;
                    case TermKind.NoReturn:
                        EmitNoReturn(block, frame.State, frame.Stmts);
                        frame.FallsOff = null;
                        stack.Pop();
                        break;
                    case TermKind.Trap:
                        if (block.Term is null)
                            throw new LiftException($"0x{block.Start:X}: the path from this block has no lifted end. " + string.Join(" ", _fn.Issues));
                        throw new LiftException($"0x{block.Term.Address:X}: a trap instruction (BRK/UDF/HLT) is reached; no C# equivalent is invented.");
                    case TermKind.Jump:
                    case TermKind.Fall:
                        frame.Block = Next(block.Next);
                        break;
                    case TermKind.Cond:
                        frame.Condition = BranchCondition(block.Term!, frame.State);
                        frame.Merge = _ipdom.TryGetValue(block.Start, out var merge) ? merge : null;
                        frame.TermAddress = block.Term!.Address;
                        frame.Taken = new RegionFrame { Block = Next(block.Taken), Stop = frame.Merge, State = frame.State.Clone() };
                        frame.Fall = new RegionFrame { Block = Next(block.Next), Stop = frame.Merge, State = frame.State.Clone() };
                        frame.Phase = RegionPhase.Join;
                        frame.Arm = 0;
                        stack.Push(frame.Taken);
                        break;
                }
            }
            fallsOff = root.FallsOff;
            return root.Stmts;
        }

        private bool FinishCond(RegionFrame frame)
        {
            var taken = frame.Taken!;
            var fall = frame.Fall!;
            var takenEnd = taken.FallsOff;
            var fallEnd = fall.FallsOff;
            if (takenEnd is null && fallEnd is null)
            {
                frame.Stmts.Add(new IfStmt(frame.Condition!, taken.Stmts, fall.Stmts) { Addresses = [frame.TermAddress] });
                frame.FallsOff = null;
                return true;
            }
            var merged = takenEnd is null ? fallEnd! : fallEnd is null ? takenEnd : Merge(takenEnd, fallEnd, taken.Stmts, fall.Stmts, frame.Stmts, frame.TermAddress);
            if (taken.Stmts.Count == 0 && fall.Stmts.Count > 0)
                frame.Stmts.Add(new IfStmt(Simplifier.Not(frame.Condition!), fall.Stmts, []) { Addresses = [frame.TermAddress] });
            else if (taken.Stmts.Count > 0 || fall.Stmts.Count > 0)
                frame.Stmts.Add(new IfStmt(frame.Condition!, taken.Stmts, fall.Stmts) { Addresses = [frame.TermAddress] });
            if (frame.Merge is null)
                throw new LiftException($"0x{frame.TermAddress:X}: a branch arm continues but the arms have no common continuation.");
            frame.State = merged;
            frame.Block = Next(frame.Merge.Value);
            return false;
        }

        private Block Next(ulong address) =>
            _blockAt.TryGetValue(address, out var b) ? b : throw new LiftException($"0x{address:X}: branch target is not a discovered block.");

        private State Merge(State a, State b, List<Stmt> armA, List<Stmt> armB, List<Stmt> before, ulong address)
        {
            if (a.Sp != b.Sp)
                throw new LiftException($"0x{address:X}: the stack pointer differs between branch arms.");
            var merged = a.Clone();
            merged.Flags = Equals(a.Flags, b.Flags) ? a.Flags : null;
            void MergeSlots(Slot?[] left, Slot?[] right, Slot?[] target, string prefix)
            {
                for (var r = 0; r < left.Length; r++)
                {
                    var l = left[r];
                    var rr = right[r];
                    if (l is null || rr is null)
                    {
                        target[r] = null;
                        continue;
                    }
                    if (l.Width == rr.Width && Equals(l.Value, rr.Value))
                        continue;
                    if (l.Width != rr.Width || l.Value is OpaqueExpr or StackAddrExpr or FieldAddrExpr or AddressExpr || rr.Value is OpaqueExpr or StackAddrExpr or FieldAddrExpr or AddressExpr)
                    {
                        target[r] = null;
                        continue;
                    }
                    var type = Unify(l.Value, rr.Value);
                    if (type is null)
                    {
                        target[r] = null;
                        continue;
                    }
                    var refType = type == PrimitiveKind.Reference && l.Value.RefType == rr.Value.RefType ? l.Value.RefType : null;
                    if (type == PrimitiveKind.Reference && refType is null)
                    {
                        target[r] = null;
                        continue;
                    }
                    var name = "m" + prefix + r + "_" + (++_temp);
                    before.Add(new DeclareStmt(name, type.Value, new DefaultExpr(type.Value) { RefType = refType }) { Addresses = [address], TypeName = refType });
                    armA.Add(new AssignStmt(name, l.Value) { Addresses = [address] });
                    armB.Add(new AssignStmt(name, rr.Value) { Addresses = [address] });
                    target[r] = new Slot { Value = new LocalExpr(name, type.Value) { RefType = refType }, Width = l.Width };
                }
            }
            MergeSlots(a.X, b.X, merged.X, "x");
            MergeSlots(a.V, b.V, merged.V, "v");
            foreach (var key in merged.Stack.Keys.ToList())
            {
                if (!b.Stack.TryGetValue(key, out var other) || !Equals(other.Value, merged.Stack[key].Value) || other.Width != merged.Stack[key].Width)
                    merged.Stack.Remove(key);
            }
            return merged;
        }

        private static PrimitiveKind? Unify(Expr a, Expr b)
        {
            if (a.Type == b.Type)
                return a.Type;
            if (a.Type is PrimitiveKind.Int32 or PrimitiveKind.UInt32 && b.Type is PrimitiveKind.Int32 or PrimitiveKind.UInt32)
                return PrimitiveKind.Int32;
            if (a.Type is PrimitiveKind.Int64 or PrimitiveKind.UInt64 && b.Type is PrimitiveKind.Int64 or PrimitiveKind.UInt64)
                return PrimitiveKind.Int64;
            return null;
        }

        private void CheckFrame(State st, ulong address, string what)
        {
            if (st.Sp != 0)
                throw new LiftException($"0x{address:X}: {what} with the stack pointer at entry{st.Sp:+#;-#;+0}. The frame was not restored.");
            if (st.X[30]?.Value is not OpaqueExpr { Tag: "return address" })
                throw new LiftException($"0x{address:X}: {what} without the original return address in x30.");
            for (var r = 19; r <= 29; r++)
            {
                var expected = r == 29 ? "caller frame pointer" : "caller x" + r;
                if (st.X[r]?.Value is not OpaqueExpr o || o.Tag != expected)
                    throw new LiftException($"0x{address:X}: {what} with callee-saved x{r} not restored.");
            }
        }

        private void EmitReturn(A64Op op, State st, List<Stmt> stmts)
        {
            if (op.Rn != 30)
                throw new LiftException($"0x{op.Address:X}: RET through x{op.Rn} is not lifted.");
            CheckFrame(st, op.Address, "return");
            var ret = _req.Contract.ReturnType;
            if (ret == PrimitiveKind.Void)
            {
                stmts.Add(new ReturnStmt(null) { Addresses = [op.Address] });
                return;
            }
            if (ret is PrimitiveKind.Single or PrimitiveKind.Double)
            {
                var v = st.V[0] ?? throw new LiftException($"0x{op.Address:X}: the floating-point return register holds no lifted value.");
                if (v.Width != (ret == PrimitiveKind.Double ? 64 : 32) || v.Value is OpaqueExpr)
                    throw new LiftException($"0x{op.Address:X}: the floating-point return register does not hold a {ret} value.");
                stmts.Add(new ReturnStmt(v.Value) { Addresses = [op.Address] });
                return;
            }
            var value = Read(0, WidthOf(ret) == 64, st, op.Address);
            if (ret == PrimitiveKind.Reference && value.Type != PrimitiveKind.Reference && value is not ConstExpr { Value: 0 })
                throw new LiftException($"0x{op.Address:X}: the reference return register does not hold a lifted reference.");
            if (ret == PrimitiveKind.Reference && value is ConstExpr)
                value = new NullExpr();
            stmts.Add(new ReturnStmt(Coerce(value, ret)) { Addresses = [op.Address] });
        }

        private void EmitTailCall(A64Op op, State st, List<Stmt> stmts)
        {
            CheckFrame(st, op.Address, "tail call");
            var callee = ResolveCallee(op.Target, op.Address, out var chain);
            var call = BuildCall(callee, op.Target, st, op.Address);
            if (chain is not null)
                EvidenceNotes.Add($"0x{op.Address:X}: tail call resolved through thunk chain {chain}.");
            EvidenceNotes.Add($"0x{op.Address:X}: unconditional branch to known method entry 0x{op.Target:X} ({callee.DeclaringType}.{callee.Name}) with the frame restored is a tail call.");
            if (call is CtorInitializerStmt init)
            {
                stmts.Add(init);
                stmts.Add(new ReturnStmt(null) { Addresses = [op.Address] });
                return;
            }
            var expr = (CallExpr)call;
            var ret = _req.Contract.ReturnType;
            if (ret == PrimitiveKind.Void)
            {
                stmts.Add(new ExpressionStmt(expr) { Addresses = [op.Address] });
                stmts.Add(new ReturnStmt(null) { Addresses = [op.Address] });
                return;
            }
            if (callee.ReturnType != ret)
                throw new LiftException($"0x{op.Address:X}: tail call returns {callee.ReturnType} but this method returns {ret}.");
            stmts.Add(new ReturnStmt(expr) { Addresses = [op.Address] });
        }

        private void EmitNoReturn(Block block, State st, List<Stmt> stmts)
        {
            var op = block.Term!;
            if (_req.RuntimeHelpers is not null && _req.RuntimeHelpers.TryGetValue(op.Target, out var helper) && helper.Kind == RuntimeHelperKind.RaiseNullReferenceException)
            {
                stmts.Add(new ThrowStmt("System.NullReferenceException", helper.Evidence) { Addresses = [op.Address] });
                EvidenceNotes.Add($"0x{op.Address:X}: call to 0x{op.Target:X} raises NullReferenceException ({helper.Evidence}).");
                return;
            }
            if (_req.CallTargets is not null && _req.CallTargets.ContainsKey(op.Target))
            {
                var callee = _req.CallTargets[op.Target];
                var call = BuildCall(callee, op.Target, st, op.Address);
                if (call is CallExpr ce)
                    stmts.Add(new ExpressionStmt(ce) { Addresses = [op.Address] });
                stmts.Add(new ThrowStmt("System.InvalidOperationException", "unreachable: the managed callee does not return") { Addresses = [op.Address] });
                return;
            }
            CandidateOnly = true;
            Notes.Add($"0x{op.Address:X}: noreturn call to 0x{op.Target:X} ({_fn.NoReturnCalls[op.Address]}). The helper is not identified, so the exception type is unknown and the body is a candidate only.");
            stmts.Add(new ThrowStmt("IL2CPP_RECOVER_UNIDENTIFIED_NORETURN_0x" + op.Target.ToString("X"), _fn.NoReturnCalls[op.Address]) { Addresses = [op.Address] });
        }

        private Expr BranchCondition(A64Op op, State st)
        {
            switch (op.Kind)
            {
                case A64Kind.Cbz:
                case A64Kind.Cbnz:
                {
                    var value = Read(op.Rd, op.Is64, st, op.Address);
                    var isZero = op.Kind == A64Kind.Cbz;
                    if (value.Type == PrimitiveKind.Reference)
                        return new BinaryExpr(isZero ? "==" : "!=", value, new NullExpr(), PrimitiveKind.Boolean);
                    if (value.Type == PrimitiveKind.Boolean)
                        return isZero ? Simplifier.Not(value) : value;
                    return new BinaryExpr(isZero ? "==" : "!=", value, new ConstExpr(0, value.Type), PrimitiveKind.Boolean);
                }
                case A64Kind.Tbz:
                case A64Kind.Tbnz:
                {
                    var value = Read(op.Rd, op.Is64, st, op.Address);
                    var clear = op.Kind == A64Kind.Tbz;
                    if (value.Type == PrimitiveKind.Boolean && op.Imm == 0)
                        return clear ? Simplifier.Not(value) : value;
                    if (value.Type is PrimitiveKind.Reference or PrimitiveKind.Boolean)
                        throw new LiftException($"0x{op.Address:X}: bit test of a {value.Type} value is not lifted.");
                    var t = op.Imm >= 32 || value.Type is PrimitiveKind.Int64 or PrimitiveKind.UInt64 ? PrimitiveKind.Int64 : PrimitiveKind.Int32;
                    var masked = new BinaryExpr("&", Coerce(value, t), new ConstExpr(1L << (int)op.Imm, t), t);
                    return new BinaryExpr(clear ? "==" : "!=", masked, new ConstExpr(0, t), PrimitiveKind.Boolean);
                }
                case A64Kind.BCond:
                    return FlagCondition(op.Cond, st, op.Address);
                default:
                    throw new LiftException($"0x{op.Address:X}: expected a conditional branch.");
            }
        }

        private Expr FlagCondition(A64Cond cond, State st, ulong address)
        {
            var f = st.Flags ?? throw new LiftException($"0x{address:X}: condition {cond} does not follow a lifted flag-setting instruction.");
            switch (f.Kind)
            {
                case FlagKind.Compare:
                {
                    if (f.Left.Type == PrimitiveKind.Reference || f.Right.Type == PrimitiveKind.Reference)
                    {
                        if (cond is A64Cond.Eq or A64Cond.Ne)
                            return new BinaryExpr(cond == A64Cond.Eq ? "==" : "!=", f.Left, f.Right is ConstExpr { Value: 0 } ? new NullExpr() : f.Right, PrimitiveKind.Boolean);
                        throw new LiftException($"0x{address:X}: ordered comparison of references is not lifted.");
                    }
                    if (f.Left.Type == PrimitiveKind.Boolean && f.Right is ConstExpr { Value: 0 } && cond is A64Cond.Eq or A64Cond.Ne)
                        return cond == A64Cond.Eq ? Simplifier.Not(f.Left) : f.Left;
                    var op = cond switch
                    {
                        A64Cond.Eq => "==", A64Cond.Ne => "!=",
                        A64Cond.Lt => "<", A64Cond.Ge => ">=", A64Cond.Gt => ">", A64Cond.Le => "<=",
                        A64Cond.Cc => "u<", A64Cond.Cs => "u>=", A64Cond.Hi => "u>", A64Cond.Ls => "u<=",
                        _ => throw new LiftException($"0x{address:X}: condition {cond} after a compare is not lifted (MI/PL/VS/VC depend on overflow).")
                    };
                    var t = f.Width == 64 ? PrimitiveKind.Int64 : PrimitiveKind.Int32;
                    return new BinaryExpr(op, Coerce(f.Left, t), Coerce(f.Right, t), PrimitiveKind.Boolean);
                }
                case FlagKind.AddCompare:
                {
                    if (cond is not (A64Cond.Eq or A64Cond.Ne))
                        throw new LiftException($"0x{address:X}: condition {cond} after CMN/ADDS is not lifted.");
                    var t = f.Width == 64 ? PrimitiveKind.Int64 : PrimitiveKind.Int32;
                    var sum = new BinaryExpr("+", Coerce(f.Left, t), Coerce(f.Right, t), t);
                    return new BinaryExpr(cond == A64Cond.Eq ? "==" : "!=", sum, new ConstExpr(0, t), PrimitiveKind.Boolean);
                }
                case FlagKind.Test:
                {
                    var t = f.Width == 64 ? PrimitiveKind.Int64 : PrimitiveKind.Int32;
                    var v = Coerce(f.Left, t);
                    var zero = new ConstExpr(0, t);
                    return cond switch
                    {
                        A64Cond.Eq => new BinaryExpr("==", v, zero, PrimitiveKind.Boolean),
                        A64Cond.Ne => new BinaryExpr("!=", v, zero, PrimitiveKind.Boolean),
                        A64Cond.Mi or A64Cond.Lt => new BinaryExpr("<", v, zero, PrimitiveKind.Boolean),
                        A64Cond.Pl or A64Cond.Ge => new BinaryExpr(">=", v, zero, PrimitiveKind.Boolean),
                        A64Cond.Gt => new BinaryExpr(">", v, zero, PrimitiveKind.Boolean),
                        A64Cond.Le => new BinaryExpr("<=", v, zero, PrimitiveKind.Boolean),
                        _ => throw new LiftException($"0x{address:X}: condition {cond} after TST/ANDS is not lifted.")
                    };
                }
                case FlagKind.FloatCompare:
                {
                    Expr Cmp(string o) => new BinaryExpr(o, f.Left, f.Right, PrimitiveKind.Boolean);
                    return cond switch
                    {
                        A64Cond.Eq => Cmp("=="),
                        A64Cond.Ne => Cmp("!="),
                        A64Cond.Gt => Cmp(">"),
                        A64Cond.Ge => Cmp(">="),
                        A64Cond.Mi or A64Cond.Cc => Cmp("<"),
                        A64Cond.Ls => Cmp("<="),
                        A64Cond.Lt => Simplifier.Not(Cmp(">=")),
                        A64Cond.Le => Simplifier.Not(Cmp(">")),
                        A64Cond.Hi => Simplifier.Not(Cmp("<=")),
                        A64Cond.Pl or A64Cond.Cs => Simplifier.Not(Cmp("<")),
                        _ => throw new LiftException($"0x{address:X}: floating-point condition {cond} is not lifted.")
                    };
                }
            }
            throw new LiftException($"0x{address:X}: unknown flag state.");
        }

        private Expr Read(int r, bool is64, State st, ulong address, bool spAllowed = false)
        {
            if (r == 31)
            {
                if (spAllowed)
                    return new StackAddrExpr(st.Sp);
                return new ConstExpr(0, is64 ? PrimitiveKind.Int64 : PrimitiveKind.Int32);
            }
            var slot = st.X[r] ?? throw new LiftException($"0x{address:X}: {(is64 ? "x" : "w")}{r} has no lifted value.");
            var v = slot.Value;
            if (v is OpaqueExpr o)
                throw new LiftException($"0x{address:X}: {(is64 ? "x" : "w")}{r} holds {o.Describe()}, which has no C# meaning here.");
            if (!is64)
            {
                if (v is StackAddrExpr or FieldAddrExpr or AddressExpr)
                    throw new LiftException($"0x{address:X}: w{r} truncates an address.");
                if (v.Type == PrimitiveKind.Reference)
                    throw new LiftException($"0x{address:X}: w{r} truncates a reference.");
                if (slot.Width == 64)
                    return v is ConstExpr c ? new ConstExpr(unchecked((int)c.Value), PrimitiveKind.Int32) : new CastExpr(v, PrimitiveKind.Int32);
                return v;
            }
            if (slot.Width == 32)
            {
                if (v is ConstExpr c32)
                    return new ConstExpr(unchecked((long)(uint)c32.Value), PrimitiveKind.Int64);
                if (v.Type == PrimitiveKind.Boolean)
                    return new CastExpr(v, PrimitiveKind.Int64);
                return new CastExpr(new CastExpr(v, PrimitiveKind.UInt32), PrimitiveKind.Int64);
            }
            return v;
        }

        private static void Write(State st, int r, int width, Expr value)
        {
            if (r == 31)
                return;
            st.X[r] = new Slot { Value = value, Width = width };
        }

        private static Expr Coerce(Expr e, PrimitiveKind target)
        {
            if (e.Type == target)
                return e;
            if (e is ConstExpr c && target is PrimitiveKind.Int32 or PrimitiveKind.Int64 or PrimitiveKind.UInt32 or PrimitiveKind.UInt64)
                return new ConstExpr(target is PrimitiveKind.Int32 ? unchecked((int)c.Value) : c.Value, target);
            if (target == PrimitiveKind.Boolean && e.Type is PrimitiveKind.Int32 or PrimitiveKind.Int64 or PrimitiveKind.UInt32 or PrimitiveKind.UInt64)
                return new BinaryExpr("!=", e, new ConstExpr(0, e.Type), PrimitiveKind.Boolean);
            if (e.Type == PrimitiveKind.Reference || target == PrimitiveKind.Reference)
                return e;
            return new CastExpr(e, target);
        }

        private Expr ReadFloat(int r, bool isDouble, State st, ulong address)
        {
            var slot = st.V[r] ?? throw new LiftException($"0x{address:X}: {(isDouble ? "d" : "s")}{r} has no lifted value.");
            if (slot.Value is OpaqueExpr o)
                throw new LiftException($"0x{address:X}: {(isDouble ? "d" : "s")}{r} holds {o.Describe()}.");
            if (slot.Width != (isDouble ? 64 : 32))
                throw new LiftException($"0x{address:X}: {(isDouble ? "d" : "s")}{r} is read at a different width than it was written.");
            return slot.Value;
        }

        private Expr Operand2(A64Op op, State st, bool is64)
        {
            if (op.HasImm)
                return new ConstExpr(op.Imm, is64 ? PrimitiveKind.Int64 : PrimitiveKind.Int32);
            var t = is64 ? PrimitiveKind.Int64 : PrimitiveKind.Int32;
            var rm = Coerce(Read(op.Rm, is64, st, op.Address), t);
            if (op.ShiftAmount != 0)
            {
                var amount = new ConstExpr(op.ShiftAmount, PrimitiveKind.Int32);
                rm = op.Shift switch
                {
                    A64Shift.Lsl => new BinaryExpr("<<", rm, amount, t),
                    A64Shift.Lsr => new BinaryExpr(">>>", rm, amount, t),
                    A64Shift.Asr => new BinaryExpr(">>", rm, amount, t),
                    _ => throw new LiftException($"0x{op.Address:X}: rotate-right operand is not lifted.")
                };
            }
            if (op.InvertRm)
                rm = Simplifier.Fold(new UnaryExpr("~", rm, t));
            return rm;
        }

        private void LiftOne(A64Op op, State st, List<Stmt> stmts)
        {
            var w = op.Is64 ? 64 : 32;
            var t = op.Is64 ? PrimitiveKind.Int64 : PrimitiveKind.Int32;
            switch (op.Kind)
            {
                case A64Kind.Nop:
                    return;
                case A64Kind.Add:
                case A64Kind.Sub:
                    LiftAddSub(op, st);
                    return;
                case A64Kind.And:
                case A64Kind.Orr:
                case A64Kind.Eor:
                    LiftLogical(op, st);
                    return;
                case A64Kind.Movz:
                    Write(st, op.Rd, w, new ConstExpr(op.Is64 ? op.Imm << op.ShiftAmount : unchecked((int)(op.Imm << op.ShiftAmount)), t));
                    return;
                case A64Kind.Movn:
                {
                    var raw = ~(op.Imm << op.ShiftAmount);
                    Write(st, op.Rd, w, new ConstExpr(op.Is64 ? raw : unchecked((int)raw), t));
                    return;
                }
                case A64Kind.Movk:
                {
                    if (st.X[op.Rd] is not { Value: ConstExpr prior } priorSlot)
                        throw new LiftException($"0x{op.Address:X}: MOVK into a register that does not hold a constant.");
                    var baseValue = priorSlot.Width == 32 ? (long)(uint)prior.Value : prior.Value;
                    var mask = 0xFFFFL << op.ShiftAmount;
                    var value = (baseValue & ~mask) | (op.Imm << op.ShiftAmount);
                    Write(st, op.Rd, w, new ConstExpr(op.Is64 ? value : unchecked((int)value), t));
                    return;
                }
                case A64Kind.Ubfm:
                case A64Kind.Sbfm:
                    LiftBitfield(op, st);
                    return;
                case A64Kind.Csel:
                case A64Kind.Csinc:
                case A64Kind.Csinv:
                case A64Kind.Csneg:
                {
                    var cond = FlagCondition(op.Cond, st, op.Address);
                    var a = Coerce(Read(op.Rn, op.Is64, st, op.Address), t);
                    var b = Coerce(Read(op.Rm, op.Is64, st, op.Address), t);
                    Expr other = op.Kind switch
                    {
                        A64Kind.Csel => b,
                        A64Kind.Csinc => Simplifier.Fold(new BinaryExpr("+", b, new ConstExpr(1, t), t)),
                        A64Kind.Csinv => Simplifier.Fold(new UnaryExpr("~", b, t)),
                        _ => Simplifier.Fold(new UnaryExpr("-", b, t))
                    };
                    Write(st, op.Rd, w, Simplifier.Fold(new ConditionalExpr(cond, a, other)));
                    return;
                }
                case A64Kind.Madd:
                case A64Kind.Msub:
                {
                    var n = Coerce(Read(op.Rn, op.Is64, st, op.Address), t);
                    var m = Coerce(Read(op.Rm, op.Is64, st, op.Address), t);
                    Expr product = new BinaryExpr("*", n, m, t);
                    if (op.Ra != 31)
                    {
                        var acc = Coerce(Read(op.Ra, op.Is64, st, op.Address), t);
                        product = new BinaryExpr(op.Kind == A64Kind.Madd ? "+" : "-", acc, product, t);
                    }
                    else if (op.Kind == A64Kind.Msub)
                        product = new UnaryExpr("-", product, t);
                    Write(st, op.Rd, w, Simplifier.Fold(product));
                    return;
                }
                case A64Kind.Lslv:
                case A64Kind.Lsrv:
                case A64Kind.Asrv:
                {
                    var n = Coerce(Read(op.Rn, op.Is64, st, op.Address), t);
                    var m = Coerce(Read(op.Rm, false, st, op.Address), PrimitiveKind.Int32);
                    var o = op.Kind switch { A64Kind.Lslv => "<<", A64Kind.Lsrv => ">>>", _ => ">>" };
                    Write(st, op.Rd, w, new BinaryExpr(o, n, m, t));
                    return;
                }
                case A64Kind.Udiv:
                case A64Kind.Sdiv:
                    throw new LiftException($"0x{op.Address:X}: {op.Kind.ToString().ToUpperInvariant()} is not lifted: AArch64 returns 0 on division by zero and wraps int.MinValue/-1, where C# throws.");
                case A64Kind.Adrp:
                case A64Kind.Adr:
                    Write(st, op.Rd, 64, new AddressExpr(op.Target));
                    return;
                case A64Kind.Ldr:
                case A64Kind.Str:
                    LiftLoadStore(op, st, stmts);
                    return;
                case A64Kind.Ldp:
                case A64Kind.Stp:
                    LiftPair(op, st);
                    return;
                case A64Kind.Bl:
                    LiftCall(op, st, stmts);
                    return;
                case A64Kind.Blr:
                    throw new LiftException($"0x{op.Address:X}: indirect call through x{op.Rn} (virtual or interface dispatch, a delegate, or a runtime function pointer) is not resolved.");
                case A64Kind.FmovReg:
                    st.V[op.Rd] = new Slot { Value = ReadFloat(op.Rn, op.FloatDouble, st, op.Address), Width = op.FloatDouble ? 64 : 32 };
                    return;
                case A64Kind.FmovImm:
                    st.V[op.Rd] = new Slot { Value = new FloatConstExpr(op.FloatImm, op.FloatDouble ? PrimitiveKind.Double : PrimitiveKind.Single), Width = op.FloatDouble ? 64 : 32 };
                    return;
                case A64Kind.Fadd:
                case A64Kind.Fsub:
                case A64Kind.Fmul:
                case A64Kind.Fdiv:
                {
                    var ft = op.FloatDouble ? PrimitiveKind.Double : PrimitiveKind.Single;
                    var a = ReadFloat(op.Rn, op.FloatDouble, st, op.Address);
                    var b = ReadFloat(op.Rm, op.FloatDouble, st, op.Address);
                    var o = op.Kind switch { A64Kind.Fadd => "+", A64Kind.Fsub => "-", A64Kind.Fmul => "*", _ => "/" };
                    st.V[op.Rd] = new Slot { Value = new BinaryExpr(o, a, b, ft), Width = op.FloatDouble ? 64 : 32 };
                    return;
                }
                case A64Kind.Fneg:
                {
                    var ft = op.FloatDouble ? PrimitiveKind.Double : PrimitiveKind.Single;
                    st.V[op.Rd] = new Slot { Value = new UnaryExpr("-", ReadFloat(op.Rn, op.FloatDouble, st, op.Address), ft), Width = op.FloatDouble ? 64 : 32 };
                    return;
                }
                case A64Kind.Fabs:
                {
                    var ft = op.FloatDouble ? PrimitiveKind.Double : PrimitiveKind.Single;
                    var call = new CallExpr("Abs", [ReadFloat(op.Rn, op.FloatDouble, st, op.Address)], ft) { StaticType = op.FloatDouble ? "System.Math" : "System.MathF" };
                    st.V[op.Rd] = new Slot { Value = call, Width = op.FloatDouble ? 64 : 32 };
                    return;
                }
                case A64Kind.Fcmp:
                {
                    var a = ReadFloat(op.Rn, op.FloatDouble, st, op.Address);
                    Expr b = op.CompareWithZero
                        ? new FloatConstExpr(0, op.FloatDouble ? PrimitiveKind.Double : PrimitiveKind.Single)
                        : ReadFloat(op.Rm, op.FloatDouble, st, op.Address);
                    st.Flags = new Flags(FlagKind.FloatCompare, a, b, op.FloatDouble ? 64 : 32);
                    return;
                }
                case A64Kind.Fcsel:
                {
                    var cond = FlagCondition(op.Cond, st, op.Address);
                    st.V[op.Rd] = new Slot { Value = new ConditionalExpr(cond, ReadFloat(op.Rn, op.FloatDouble, st, op.Address), ReadFloat(op.Rm, op.FloatDouble, st, op.Address)), Width = op.FloatDouble ? 64 : 32 };
                    return;
                }
                case A64Kind.Scvtf:
                case A64Kind.Ucvtf:
                {
                    var src = Coerce(Read(op.Rn, op.Is64, st, op.Address), t);
                    if (op.Kind == A64Kind.Ucvtf)
                        src = new CastExpr(src, op.Is64 ? PrimitiveKind.UInt64 : PrimitiveKind.UInt32);
                    var ft = op.FloatDouble ? PrimitiveKind.Double : PrimitiveKind.Single;
                    st.V[op.Rd] = new Slot { Value = new CastExpr(src, ft), Width = op.FloatDouble ? 64 : 32 };
                    return;
                }
                case A64Kind.Fcvtzs:
                    Write(st, op.Rd, w, new CastExpr(ReadFloat(op.Rn, op.FloatDouble, st, op.Address), t));
                    return;
                case A64Kind.FcvtToDouble:
                    st.V[op.Rd] = new Slot { Value = new CastExpr(ReadFloat(op.Rn, false, st, op.Address), PrimitiveKind.Double), Width = 64 };
                    return;
                case A64Kind.FcvtToSingle:
                    st.V[op.Rd] = new Slot { Value = new CastExpr(ReadFloat(op.Rn, true, st, op.Address), PrimitiveKind.Single), Width = 32 };
                    return;
                default:
                    throw new LiftException($"0x{op.Address:X}: instruction 0x{op.Word:X8} ({op.Kind}) is not in the lifted subset.");
            }
        }

        private void LiftAddSub(A64Op op, State st)
        {
            var w = op.Is64 ? 64 : 32;
            var t = op.Is64 ? PrimitiveKind.Int64 : PrimitiveKind.Int32;
            var rnExpr = Read(op.Rn, op.Is64, st, op.Address, spAllowed: op.HasImm);
            var sub = op.Kind == A64Kind.Sub;
            if (rnExpr is StackAddrExpr sa && op.HasImm)
            {
                var off = sa.Offset + (sub ? -op.Imm : op.Imm);
                if (op.SetsFlags)
                    throw new LiftException($"0x{op.Address:X}: flag-setting arithmetic on the stack pointer is not lifted.");
                if (op.Rd == 31)
                    st.Sp = off;
                else
                    Write(st, op.Rd, 64, new StackAddrExpr(off));
                return;
            }
            if (op.HasImm && !op.SetsFlags && op.Rd == 31)
                throw new LiftException($"0x{op.Address:X}: the stack pointer is set from a non-stack value.");
            if (rnExpr.Type == PrimitiveKind.Reference || rnExpr is FieldAddrExpr)
            {
                if (!op.HasImm || op.SetsFlags || !op.Is64)
                    throw new LiftException($"0x{op.Address:X}: arithmetic on a reference other than a constant field offset is not lifted.");
                var (b, o) = rnExpr is FieldAddrExpr fa ? (fa.Base, fa.Offset) : (rnExpr, 0L);
                Write(st, op.Rd, 64, new FieldAddrExpr(b, o + (sub ? -op.Imm : op.Imm)));
                return;
            }
            if (rnExpr is AddressExpr addr && op.HasImm && !sub && !op.SetsFlags)
            {
                Write(st, op.Rd, 64, new AddressExpr(addr.Address + (ulong)op.Imm));
                return;
            }
            if (rnExpr is StackAddrExpr or AddressExpr)
                throw new LiftException($"0x{op.Address:X}: address arithmetic of this form is not lifted.");
            var right = Operand2(op, st, op.Is64);
            if (op.SetsFlags && sub && rnExpr.Type == PrimitiveKind.Boolean && right is ConstExpr { Value: 0 })
            {
                st.Flags = new Flags(FlagKind.Compare, rnExpr, right, w);
                if (op.Rd != 31)
                    Write(st, op.Rd, w, Coerce(rnExpr, t));
                return;
            }
            var left = Coerce(rnExpr, t);
            if (op.SetsFlags)
            {
                st.Flags = sub ? new Flags(FlagKind.Compare, left, right, w) : new Flags(FlagKind.AddCompare, left, right, w);
                if (op.Rd != 31)
                    Write(st, op.Rd, w, Simplifier.Fold(new BinaryExpr(sub ? "-" : "+", left, right, t)));
                return;
            }
            Write(st, op.Rd, w, Simplifier.Fold(new BinaryExpr(sub ? "-" : "+", left, right, t)));
        }

        private void LiftLogical(A64Op op, State st)
        {
            var w = op.Is64 ? 64 : 32;
            var t = op.Is64 ? PrimitiveKind.Int64 : PrimitiveKind.Int32;
            if (op.Kind == A64Kind.Orr && op.Rn == 31 && !op.HasImm && !op.InvertRm && op.ShiftAmount == 0 && !op.SetsFlags)
            {
                if (op.Rm == 31)
                {
                    Write(st, op.Rd, w, new ConstExpr(0, t));
                    return;
                }
                var src = st.X[op.Rm] ?? throw new LiftException($"0x{op.Address:X}: x{op.Rm} has no lifted value.");
                if (op.Is64)
                    st.X[op.Rd] = new Slot { Value = src.Value, Width = src.Width };
                else
                    Write(st, op.Rd, 32, Read(op.Rm, false, st, op.Address));
                return;
            }
            var leftRaw = Read(op.Rn, op.Is64, st, op.Address);
            var right = Operand2(op, st, op.Is64);
            Expr result;
            if (leftRaw.Type == PrimitiveKind.Boolean && right is ConstExpr { Value: 1 } && op.Kind is A64Kind.And or A64Kind.Eor && !op.InvertRm)
                result = op.Kind == A64Kind.And ? leftRaw : Simplifier.Not(leftRaw);
            else
            {
                var left = Coerce(leftRaw, t);
                result = op.Rn == 31 && op.Kind == A64Kind.Orr && op.HasImm
                    ? Coerce(right, t)
                    : Simplifier.Fold(new BinaryExpr(op.Kind switch { A64Kind.And => "&", A64Kind.Orr => "|", _ => "^" }, left, right, t));
            }
            if (op.SetsFlags)
                st.Flags = new Flags(FlagKind.Test, Coerce(result, t), new ConstExpr(0, t), w);
            if (!op.SetsFlags || op.Rd != 31)
                Write(st, op.Rd, w, result);
        }

        private void LiftBitfield(A64Op op, State st)
        {
            var width = op.Is64 ? 64 : 32;
            var t = op.Is64 ? PrimitiveKind.Int64 : PrimitiveKind.Int32;
            var r = op.Immr;
            var s = op.Imms;
            var signed = op.Kind == A64Kind.Sbfm;
            if (signed && op.Is64 && r == 0 && s == 31)
            {
                Write(st, op.Rd, 64, new CastExpr(Coerce(Read(op.Rn, false, st, op.Address), PrimitiveKind.Int32), PrimitiveKind.Int64));
                return;
            }
            var x = Coerce(Read(op.Rn, op.Is64, st, op.Address), t);
            Expr Lit(long v) => new ConstExpr(v, PrimitiveKind.Int32);
            Expr Mask(int bits) => new ConstExpr(bits >= 64 ? -1L : (long)((1UL << bits) - 1), t);
            Expr result;
            if (s == width - 1)
                result = new BinaryExpr(signed ? ">>" : ">>>", x, Lit(r), t);
            else if (!signed && s + 1 == r)
                result = new BinaryExpr("<<", x, Lit(width - 1 - s), t);
            else if (r == 0 && !signed)
                result = new BinaryExpr("&", x, Mask(s + 1), t);
            else if (s >= r)
            {
                var up = width - 1 - s;
                result = new BinaryExpr(signed ? ">>" : ">>>", new BinaryExpr("<<", x, Lit(up), t), Lit(up + r), t);
            }
            else
            {
                var up = width - 1 - s;
                var field = new BinaryExpr(signed ? ">>" : ">>>", new BinaryExpr("<<", x, Lit(up), t), Lit(up), t);
                result = new BinaryExpr("<<", field, Lit(width - r), t);
            }
            Write(st, op.Rd, width, Simplifier.Fold(result));
        }

        private (Expr Base, long Offset) Address(A64Op op, State st, out long writeback, out bool isStack)
        {
            writeback = 0;
            var baseExpr = Read(op.Rn, true, st, op.Address, spAllowed: true);
            long offset;
            if (op.Index == A64Index.RegisterOffset)
            {
                var index = st.X[op.Rm]?.Value ?? throw new LiftException($"0x{op.Address:X}: index register x{op.Rm} has no lifted value.");
                if (op.Extend != 3 || op.ShiftAmount != 0)
                    throw new LiftException($"0x{op.Address:X}: extended or scaled register offsets are not lifted.");
                if (index is ConstExpr ci)
                    offset = ci.Value;
                else if (index is ConditionalExpr { WhenTrue: ConstExpr a, WhenFalse: ConstExpr b } ce)
                {
                    isStack = false;
                    return (new SelectedOffsetExpr(baseExpr, ce.Condition, a.Value, b.Value), 0);
                }
                else
                    throw new LiftException($"0x{op.Address:X}: register-offset access with a non-constant index (array or computed address) is not lifted.");
            }
            else
            {
                offset = op.Index == A64Index.Post ? 0 : op.Imm;
                if (op.Index is A64Index.Pre or A64Index.Post)
                    writeback = op.Imm;
            }
            if (baseExpr is StackAddrExpr sa)
            {
                isStack = true;
                return (sa, sa.Offset + offset);
            }
            isStack = false;
            if (writeback != 0)
                throw new LiftException($"0x{op.Address:X}: pre/post-index on a non-stack base is not lifted.");
            if (baseExpr is FieldAddrExpr fa)
                return (fa.Base, fa.Offset + offset);
            return (baseExpr, offset);
        }

        private static void ApplyWriteback(A64Op op, State st, long writeback)
        {
            if (writeback == 0)
                return;
            if (op.Rn == 31)
                st.Sp += writeback;
            else if (st.X[op.Rn]?.Value is StackAddrExpr sa)
                Write(st, op.Rn, 64, new StackAddrExpr(sa.Offset + writeback));
        }

        private void LiftLoadStore(A64Op op, State st, List<Stmt> stmts)
        {
            var (baseExpr, offset) = Address(op, st, out var writeback, out var isStack);
            if (isStack)
            {
                if (op.Kind == A64Kind.Str)
                    st.Stack[offset] = new Slot { Value = op.Vector ? ReadVector(op.Rt, st, op.Address) : ReadGpr(op.Rt, op.Size, st, op.Address), Width = op.Size * 8 };
                else
                    LoadStack(op.Rt, op.Vector, op.Size, offset, op.SignedLoad, st, op.Address);
                ApplyWriteback(op, st, writeback);
                return;
            }
            if (baseExpr is SelectedOffsetExpr sel)
            {
                LiftSelected(op, sel, st, stmts);
                return;
            }
            if (baseExpr is AddressExpr global)
            {
                var staticField = _req.Contract.Fields.FirstOrDefault(f => f.IsStatic && f.AbsoluteAddress == global.Address + (ulong)offset);
                if (staticField is null)
                    throw new LiftException($"0x{op.Address:X}: access to global data at 0x{global.Address + (ulong)offset:X} (IL2CPP metadata usage, class pointer, or static storage) is not resolved. No pointer was invented.");
                AccessField(op, null, staticField, st, stmts);
                return;
            }
            if (baseExpr.Type != PrimitiveKind.Reference)
                throw new LiftException($"0x{op.Address:X}: memory access through a non-reference value is not lifted. No pointer was invented.");
            if (offset == 0)
                throw new LiftException($"0x{op.Address:X}: read of the object header (Il2CppClass pointer) indicates a type check or virtual dispatch, which is not lifted.");
            var fields = FieldsOf(baseExpr);
            var field = fields.FirstOrDefault(f => !f.IsStatic && f.InstanceOffset == offset);
            var wider = field is null || SizeOf(field.Type) < op.Size;
            if (wider && op.Kind == A64Kind.Str && !op.Vector && (op.Rt == 31 || st.X[op.Rt] is { Value: ConstExpr }))
            {
                var c = op.Rt == 31 ? 0 : ((ConstExpr)st.X[op.Rt]!.Value).Value;
                if (TrySplit(baseExpr, offset, op.Size, c, fields, stmts, op.Address))
                    return;
            }
            if (field is null)
                throw new LiftException($"0x{op.Address:X}: no field of {baseExpr.RefType ?? "this reference"} at offset 0x{offset:X} is known. No pointer was invented.");
            AccessField(op, baseExpr, field, st, stmts);
        }

        private IReadOnlyList<FieldContract> FieldsOf(Expr reference)
        {
            if (reference is ThisExpr || reference.RefType == _selfType)
                return _req.Contract.Fields;
            if (reference.RefType is not null && _req.TypeFields is not null && _req.TypeFields.TryGetValue(reference.RefType, out var fields))
                return fields;
            return [];
        }

        private static int SizeOf(PrimitiveKind k) => k switch
        {
            PrimitiveKind.Boolean => 1,
            PrimitiveKind.Int32 or PrimitiveKind.UInt32 or PrimitiveKind.Single => 4,
            _ => 8
        };

        private bool TrySplit(Expr baseExpr, long offset, int size, long value, IReadOnlyList<FieldContract> fields, List<Stmt> stmts, ulong address)
        {
            var covered = new List<(FieldContract Field, long At)>();
            var cursor = offset;
            while (cursor < offset + size)
            {
                var f = fields.FirstOrDefault(x => !x.IsStatic && x.InstanceOffset == cursor);
                if (f is null)
                    return false;
                covered.Add((f, cursor));
                cursor += SizeOf(f.Type);
            }
            if (cursor != offset + size || covered.Count < 2)
                return false;
            foreach (var (f, at) in covered)
            {
                var shift = (int)(at - offset) * 8;
                var bits = (ulong)value >> shift;
                Expr? v = f.Type switch
                {
                    PrimitiveKind.Single => new FloatConstExpr(BitConverter.Int32BitsToSingle(unchecked((int)(uint)bits)), PrimitiveKind.Single),
                    PrimitiveKind.Double => new FloatConstExpr(BitConverter.Int64BitsToDouble((long)bits), PrimitiveKind.Double),
                    PrimitiveKind.Boolean when (bits & 0xFF) <= 1 => new ConstExpr((long)(bits & 1), PrimitiveKind.Boolean),
                    PrimitiveKind.Int32 => new ConstExpr(unchecked((int)(uint)bits), PrimitiveKind.Int32),
                    PrimitiveKind.UInt32 => new ConstExpr((long)(uint)bits, PrimitiveKind.UInt32),
                    PrimitiveKind.Reference when bits == 0 => new NullExpr(),
                    PrimitiveKind.Int64 or PrimitiveKind.UInt64 => new ConstExpr((long)bits, f.Type),
                    _ => null
                };
                if (v is null)
                    return false;
                stmts.Add(new StoreFieldStmt(baseExpr, f.Name, v) { Addresses = [address] });
            }
            EvidenceNotes.Add($"0x{address:X}: one {size * 8}-bit constant store was split across fields {string.Join(", ", covered.Select(c => c.Field.Name))} by metadata offsets.");
            return true;
        }

        private void AccessField(A64Op op, Expr? instance, FieldContract field, State st, List<Stmt> stmts)
        {
            var size = SizeOf(field.Type);
            if (op.Size != size)
                throw new LiftException($"0x{op.Address:X}: a {op.Size}-byte access does not match field {field.Name} ({field.Type}, {size} bytes).");
            var isFloat = field.Type is PrimitiveKind.Single or PrimitiveKind.Double;
            if (isFloat != op.Vector)
                throw new LiftException($"0x{op.Address:X}: register class does not match field {field.Name} ({field.Type}).");
            if (op.Kind == A64Kind.Str)
            {
                Expr value;
                if (op.Vector)
                    value = ReadFloat(op.Rt, field.Type == PrimitiveKind.Double, st, op.Address);
                else if (op.Rt == 31)
                    value = field.Type switch { PrimitiveKind.Reference => new NullExpr(), PrimitiveKind.Boolean => new ConstExpr(0, PrimitiveKind.Boolean), _ => new ConstExpr(0, field.Type) };
                else
                {
                    var raw = Read(op.Rt, size == 8, st, op.Address);
                    value = field.Type switch
                    {
                        PrimitiveKind.Reference when raw.Type == PrimitiveKind.Reference => raw,
                        PrimitiveKind.Reference when raw is ConstExpr { Value: 0 } => new NullExpr(),
                        PrimitiveKind.Reference => throw new LiftException($"0x{op.Address:X}: a non-reference value is stored into reference field {field.Name}."),
                        PrimitiveKind.Boolean => raw.Type == PrimitiveKind.Boolean ? raw : raw is ConstExpr cb && cb.Value is 0 or 1 ? new ConstExpr(cb.Value, PrimitiveKind.Boolean) : new BinaryExpr("!=", Coerce(raw, PrimitiveKind.Int32), new ConstExpr(0, PrimitiveKind.Int32), PrimitiveKind.Boolean),
                        _ => Coerce(raw, field.Type)
                    };
                }
                stmts.Add(new StoreFieldStmt(instance, field.Name, value) { Addresses = [op.Address] });
                return;
            }
            var load = new FieldExpr(instance, field.Name, field.Type) { RefType = field.Type == PrimitiveKind.Reference ? field.DeclaredType : null };
            var temp = NewTemp(load, op.Address, stmts);
            if (op.Vector)
                st.V[op.Rt] = new Slot { Value = temp, Width = size * 8 };
            else
            {
                if (op.SignedLoad && field.Type is not (PrimitiveKind.Int32 or PrimitiveKind.Int64))
                    throw new LiftException($"0x{op.Address:X}: sign-extending load of {field.Type} field {field.Name} is not lifted.");
                Write(st, op.Rt, field.Type is PrimitiveKind.Boolean ? 32 : op.Is64 || size == 8 ? 64 : 32, op.SignedLoad && op.Is64 && size == 4 ? new CastExpr(temp, PrimitiveKind.Int64) : temp);
            }
        }

        private void LiftSelected(A64Op op, SelectedOffsetExpr sel, State st, List<Stmt> stmts)
        {
            if (sel.Base.Type != PrimitiveKind.Reference)
                throw new LiftException($"0x{op.Address:X}: selected-offset access through a non-reference base is not lifted.");
            var fields = FieldsOf(sel.Base);
            var fa = fields.FirstOrDefault(f => !f.IsStatic && f.InstanceOffset == sel.WhenTrue);
            var fb = fields.FirstOrDefault(f => !f.IsStatic && f.InstanceOffset == sel.WhenFalse);
            if (fa is null || fb is null || fa.Type != fb.Type || SizeOf(fa.Type) != op.Size || op.Vector != (fa.Type is PrimitiveKind.Single or PrimitiveKind.Double))
                throw new LiftException($"0x{op.Address:X}: a field selected by condition does not resolve to two fields of the accessed size.");
            if (op.Kind == A64Kind.Ldr)
            {
                var value = new ConditionalExpr(sel.Condition, new FieldExpr(sel.Base, fa.Name, fa.Type), new FieldExpr(sel.Base, fb.Name, fb.Type));
                var temp = NewTemp(value, op.Address, stmts);
                if (op.Vector)
                    st.V[op.Rt] = new Slot { Value = temp, Width = op.Size * 8 };
                else
                    Write(st, op.Rt, op.Size == 8 ? 64 : 32, temp);
                EvidenceNotes.Add($"0x{op.Address:X}: register-offset access with a conditionally selected offset reads {fa.Name} or {fb.Name}.");
                return;
            }
            var stored = op.Vector ? ReadFloat(op.Rt, op.Size == 8, st, op.Address) : Coerce(Read(op.Rt, op.Size == 8, st, op.Address), fa.Type);
            stmts.Add(new IfStmt(sel.Condition,
                new List<Stmt> { new StoreFieldStmt(sel.Base, fa.Name, Simplifier.Assume(stored, sel.Condition, true)) { Addresses = [op.Address] } },
                new List<Stmt> { new StoreFieldStmt(sel.Base, fb.Name, Simplifier.Assume(stored, sel.Condition, false)) { Addresses = [op.Address] } })
            { Addresses = [op.Address] });
        }

        private Expr ReadGpr(int rt, int size, State st, ulong address)
        {
            if (rt == 31)
                return new ConstExpr(0, size == 8 ? PrimitiveKind.Int64 : PrimitiveKind.Int32);
            return st.X[rt]?.Value ?? throw new LiftException($"0x{address:X}: x{rt} has no lifted value to spill.");
        }

        private Expr ReadVector(int rt, State st, ulong address) =>
            st.V[rt]?.Value ?? throw new LiftException($"0x{address:X}: v{rt} has no lifted value to spill.");

        private void LoadStack(int rt, bool vector, int size, long offset, bool signed, State st, ulong address)
        {
            if (!st.Stack.TryGetValue(offset, out var slot) || slot.Width != size * 8)
                throw new LiftException($"0x{address:X}: stack slot sp{offset:+#;-#;+0} was not written by this method (stack-passed arguments or a partial spill are not lifted).");
            if (signed)
                throw new LiftException($"0x{address:X}: sign-extending reload from the stack is not lifted.");
            if (vector)
                st.V[rt] = new Slot { Value = slot.Value, Width = slot.Width };
            else if (rt != 31)
                st.X[rt] = new Slot { Value = slot.Value, Width = slot.Width };
        }

        private void LiftPair(A64Op op, State st)
        {
            var baseExpr = Read(op.Rn, true, st, op.Address, spAllowed: true);
            if (baseExpr is not StackAddrExpr sa)
                throw new LiftException($"0x{op.Address:X}: load/store pair on a non-stack base (a struct copy or two fields at once) is not lifted.");
            var start = sa.Offset + (op.Index == A64Index.Post ? 0 : op.Imm);
            if (op.Kind == A64Kind.Stp)
            {
                st.Stack[start] = new Slot { Value = op.Vector ? ReadVector(op.Rt, st, op.Address) : ReadGpr(op.Rt, op.Size, st, op.Address), Width = op.Size * 8 };
                st.Stack[start + op.Size] = new Slot { Value = op.Vector ? ReadVector(op.Rt2, st, op.Address) : ReadGpr(op.Rt2, op.Size, st, op.Address), Width = op.Size * 8 };
            }
            else
            {
                LoadStack(op.Rt, op.Vector, op.Size, start, false, st, op.Address);
                LoadStack(op.Rt2, op.Vector, op.Size, start + op.Size, false, st, op.Address);
            }
            if (op.Index is A64Index.Pre or A64Index.Post)
            {
                if (op.Rn == 31)
                    st.Sp += op.Imm;
                else
                    Write(st, op.Rn, 64, new StackAddrExpr(sa.Offset + op.Imm));
            }
        }

        private MethodContract ResolveCallee(ulong target, ulong site, out string? chain)
        {
            chain = null;
            if (_req.CallTargets is not null && _req.CallTargets.TryGetValue(target, out var callee))
                return callee;
            if (_req.Space is not null && _req.CallTargets is not null)
            {
                var resolution = FunctionDiscovery.ResolveThunk(_req.Space, target, a => _req.CallTargets.ContainsKey(a));
                if (resolution.Resolved)
                {
                    chain = resolution.Describe();
                    return _req.CallTargets[resolution.Target!.Value];
                }
            }
            var where = _req.Space is null ? "outside the supplied slice" : FunctionDiscovery.Describe(_req.Space, _fn.Entry, target);
            throw new LiftException($"0x{site:X}: call target 0x{target:X} is not in the method map ({where}). IL2CPP runtime helpers and generic instances are not identified in this binary.");
        }

        private void LiftCall(A64Op op, State st, List<Stmt> stmts)
        {
            var callee = ResolveCallee(op.Target, op.Address, out var chain);
            if (chain is not null)
                EvidenceNotes.Add($"0x{op.Address:X}: call resolved through thunk chain {chain}.");
            var call = BuildCall(callee, op.Target, st, op.Address);
            for (var r = 0; r <= 18; r++)
                st.X[r] = null;
            st.X[30] = new Slot { Value = new OpaqueExpr("clobbered link register"), Width = 64 };
            for (var r = 0; r <= 7; r++)
                st.V[r] = null;
            for (var r = 16; r <= 31; r++)
                st.V[r] = null;
            st.Flags = null;
            if (call is CtorInitializerStmt init)
            {
                stmts.Add(init);
                return;
            }
            var expr = (CallExpr)call;
            if (callee.ReturnType == PrimitiveKind.Void)
            {
                stmts.Add(new ExpressionStmt(expr) { Addresses = [op.Address] });
                return;
            }
            var temp = NewTemp(expr, op.Address, stmts);
            if (callee.ReturnType is PrimitiveKind.Single or PrimitiveKind.Double)
                st.V[0] = new Slot { Value = temp, Width = callee.ReturnType == PrimitiveKind.Double ? 64 : 32 };
            else
                st.X[0] = new Slot { Value = temp, Width = WidthOf(callee.ReturnType) };
        }

        private object BuildCall(MethodContract callee, ulong target, State st, ulong site)
        {
            if (callee.UnsupportedReason is not null)
                throw new LiftException($"0x{site:X}: call to {callee.DeclaringType}.{callee.Name} is not lifted: {callee.UnsupportedReason}.");
            if (callee.IsGeneric)
                throw new LiftException($"0x{site:X}: call to generic method {callee.DeclaringType}.{callee.Name}: generic instantiation context is not reconstructed.");
            var ngrn = 0;
            var nsrn = 0;
            Expr? receiver = null;
            if (!callee.IsStatic)
            {
                receiver = Read(0, true, st, site);
                if (receiver.Type != PrimitiveKind.Reference)
                    throw new LiftException($"0x{site:X}: the receiver for {callee.DeclaringType}.{callee.Name} is not a lifted reference.");
                ngrn = 1;
            }
            var args = new List<Expr>();
            foreach (var p in callee.Parameters)
            {
                if (p.Type is PrimitiveKind.Single or PrimitiveKind.Double)
                {
                    if (nsrn >= 8)
                        throw new LiftException($"0x{site:X}: stack-passed arguments are not lifted.");
                    args.Add(ReadFloat(nsrn++, p.Type == PrimitiveKind.Double, st, site));
                }
                else
                {
                    if (ngrn >= 8)
                        throw new LiftException($"0x{site:X}: stack-passed arguments are not lifted.");
                    var value = Read(ngrn++, WidthOf(p.Type) == 64, st, site);
                    if (p.Type == PrimitiveKind.Reference)
                    {
                        if (value is ConstExpr { Value: 0 })
                            value = new NullExpr();
                        else if (value.Type != PrimitiveKind.Reference)
                            throw new LiftException($"0x{site:X}: argument {p.Name} of {callee.Name} is not a lifted reference.");
                    }
                    else
                        value = p.Type == PrimitiveKind.Boolean && value.Type != PrimitiveKind.Boolean
                            ? value is ConstExpr cb && cb.Value is 0 or 1 ? new ConstExpr(cb.Value, PrimitiveKind.Boolean) : Coerce(value, PrimitiveKind.Boolean)
                            : Coerce(value, p.Type);
                    args.Add(value);
                }
            }
            var calleeType = Qualified(callee.Namespace, callee.DeclaringType);
            if (callee.Name == ".ctor" && receiver is ThisExpr && _req.Contract.Name == ".ctor")
            {
                var isBase = calleeType != _selfType;
                EvidenceNotes.Add($"0x{site:X}: call to {calleeType}::.ctor on this inside a constructor is the {(isBase ? "base" : "this")} constructor initializer.");
                return new CtorInitializerStmt(isBase, args) { Addresses = [site] };
            }
            if (callee.Name is ".ctor" or ".cctor")
                throw new LiftException($"0x{site:X}: a direct constructor call outside an object-creation pattern is not lifted.");
            return new CallExpr(callee.Name, args, callee.ReturnType)
            {
                Receiver = receiver,
                StaticType = callee.IsStatic ? calleeType : null,
                Target = target,
                RefType = callee.ReturnType == PrimitiveKind.Reference ? callee.DeclaredReturnType : null
            };
        }

        private LocalExpr NewTemp(Expr value, ulong address, List<Stmt> stmts)
        {
            var name = "t" + (++_temp);
            stmts.Add(new DeclareStmt(name, value.Type, value) { Addresses = [address], TypeName = value.RefType });
            return new LocalExpr(name, value.Type) { RefType = value.RefType };
        }
    }
}

public sealed record NullExpr() : Expr(PrimitiveKind.Reference)
{
    public override string Describe() => "null";
}

public sealed record DefaultExpr(PrimitiveKind Of) : Expr(Of)
{
    public override string Describe() => "default";
}

public sealed record SelectedOffsetExpr(Expr Base, Expr Condition, long WhenTrue, long WhenFalse) : Expr(PrimitiveKind.UInt64);
