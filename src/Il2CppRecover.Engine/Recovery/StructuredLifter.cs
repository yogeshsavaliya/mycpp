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
        var elision = new List<string>();
        MetadataInitGuard.Elide(blocks, blockAt, elision);
        if (Il2CppClassLayout.TryGet(request.MetadataVersion, out var staticFields, out var cctorOffset))
            ClassInitGuard.Elide(blocks, blockAt, request.ResolveMetadataSlot, staticFields, cctorOffset, elision);
        if (HasCycle(blocks, blockAt, request))
        {
            var loop = new Outcome { HasLoop = true, BlockCount = blocks.Count };
            loop.Evidence.AddRange(elision);
            return loop;
        }
        if (blocks.Count > request.Budget.MaxBlocks)
        {
            var limited = new Outcome { BlockCount = blocks.Count };
            limited.Diagnostics.Add($"AnalysisBudgetExceeded: {blocks.Count} basic blocks exceeds the limit of {request.Budget.MaxBlocks}.");
            limited.Evidence.AddRange(elision);
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
            ElideClassInit(body, ctx.EvidenceNotes);
            var ok = new Outcome { Statements = body, CandidateOnly = ctx.CandidateOnly, BlockCount = blocks.Count };
            ok.Diagnostics.AddRange(ctx.Notes);
            ok.Evidence.AddRange(elision);
            ok.Evidence.AddRange(ctx.EvidenceNotes);
            return ok;
        }
        catch (LiftException ex)
        {
            var failed = new Outcome { BlockCount = blocks.Count };
            failed.Diagnostics.Add(ex.Message);
            failed.Evidence.AddRange(elision);
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
        public List<MetadataSlot> ClassInits = [];
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

    private static void ElideClassInit(List<Stmt> body, List<string> evidence)
    {
        for (var i = 0; i < body.Count; i++)
        {
            if (body[i] is ClassInitStmt init && i + 1 < body.Count && CoversClassInit(body[i + 1], init.TypeName))
            {
                evidence.Add("The class initializer of " + init.TypeName + " runs as part of the next static member access, so the explicit check was not emitted again.");
                body.RemoveAt(i);
                i--;
                continue;
            }
            switch (body[i])
            {
                case IfStmt branch:
                    ElideClassInit(branch.WhenTrue, evidence);
                    ElideClassInit(branch.WhenFalse, evidence);
                    break;
                case WhileStmt loop:
                    ElideClassInit(loop.Body, evidence);
                    break;
            }
        }
    }

    /// <summary>
    /// The next statement covers an explicit class init when it reads or calls a static member of
    /// that type before any other call. C# runs the type initializer at that access.
    /// </summary>
    private static bool CoversClassInit(Stmt stmt, string type)
    {
        if (stmt.StaticOwner == type)
            return true;
        return stmt switch
        {
            IfStmt branch => FirstEffect(branch.Condition, type) == true,
            ReturnStmt { Value: { } value } => FirstEffect(value, type) == true,
            StoreFieldStmt store => FirstEffect(store.Value, type) == true || store.Instance is not null && FirstEffect(store.Instance, type) == true,
            ExpressionStmt expr => FirstEffect(expr.Value, type) == true,
            AssignStmt assign => FirstEffect(assign.Value, type) == true,
            DeclareStmt declare => FirstEffect(declare.Value, type) == true,
            _ => false
        };
    }

    /// <summary>True when a static of <paramref name="type"/> is evaluated before any other call.</summary>
    private static bool? FirstEffect(Expr? expr, string type)
    {
        switch (expr)
        {
            case null:
                return null;
            case FieldExpr field when field.StaticType == type:
                return true;
            case FieldExpr field:
                return FirstEffect(field.Instance, type);
            case CallExpr call when call.StaticType == type:
                return true;
            case CallExpr call:
            {
                var receiver = FirstEffect(call.Receiver, type);
                if (receiver is not null)
                    return receiver;
                foreach (var arg in call.Args)
                {
                    var effect = FirstEffect(arg, type);
                    if (effect is not null)
                        return effect;
                }
                return false;
            }
            case BinaryExpr binary:
            {
                var left = FirstEffect(binary.Left, type);
                return left is null ? FirstEffect(binary.Right, type) : left;
            }
            case UnaryExpr unary:
                return FirstEffect(unary.Value, type);
            case CastExpr cast:
                return FirstEffect(cast.Value, type);
            case ConditionalExpr cond:
            {
                var first = FirstEffect(cond.Condition, type);
                if (first is not null)
                    return first;
                var whenTrue = FirstEffect(cond.WhenTrue, type);
                return whenTrue is null ? FirstEffect(cond.WhenFalse, type) : whenTrue;
            }
            default:
                return null;
        }
    }

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
        private readonly int? _selfTypeId;
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
            _selfTypeId = req.Contract.DeclaringTypeId;
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
                st.X[0] = new Slot { Value = new ThisExpr { RefType = _selfType, RefTypeId = _selfTypeId }, Width = 64 };
                ngrn = 1;
            }
            var nsrn = 0;
            foreach (var p in _req.Contract.Parameters)
            {
                var expr = new ParamExpr(p.Name, p.Type) { RefType = p.Type == PrimitiveKind.Reference ? p.DeclaredType : null, RefTypeId = p.Type == PrimitiveKind.Reference ? p.DeclaredTypeId : null };
                if (HfaType.TryComponents(p.DeclaredType, out var incoming))
                {
                    for (var lane = 0; lane < incoming.Length; lane++)
                    {
                        if (nsrn >= 8)
                            throw new LiftException("More than eight floating-point parameters: stack-passed arguments are not lifted.");
                        st.V[nsrn++] = new Slot { Value = new FieldExpr(expr, incoming[lane], PrimitiveKind.Single), Width = 32 };
                    }
                }
                else if (p.Type is PrimitiveKind.Single or PrimitiveKind.Double)
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
                foreach (var init in block.ClassInits)
                    frame.Stmts.Add(new ClassInitStmt(init.Display) { Addresses = [block.Start] });
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
                        if (block.Term is null && frame.Stmts.LastOrDefault() is ThrowStmt)
                        {
                            frame.FallsOff = null;
                            stack.Pop();
                            break;
                        }
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
                    var sameReference = type == PrimitiveKind.Reference && TypeIdentity.Same(l.Value.RefTypeId, l.Value.RefType, rr.Value.RefTypeId, rr.Value.RefType);
                    var refType = sameReference ? l.Value.RefType : null;
                    var refTypeId = sameReference ? l.Value.RefTypeId : null;
                    if (type == PrimitiveKind.Reference && refType is null && refTypeId is null)
                    {
                        target[r] = null;
                        continue;
                    }
                    var name = "m" + prefix + r + "_" + (++_temp);
                    before.Add(new DeclareStmt(name, type.Value, new DefaultExpr(type.Value) { RefType = refType, RefTypeId = refTypeId }) { Addresses = [address], TypeName = refType });
                    armA.Add(new AssignStmt(name, l.Value) { Addresses = [address] });
                    armB.Add(new AssignStmt(name, rr.Value) { Addresses = [address] });
                    target[r] = new Slot { Value = new LocalExpr(name, type.Value) { RefType = refType, RefTypeId = refTypeId }, Width = l.Width };
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
            var call = BuildCall(callee, op.Target, st, op.Address, stmts);
            if (call is null)
                throw new LiftException($"0x{op.Address:X}: tail call to {callee.DeclaringType}.{callee.Name} did not produce a call.");
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
            if (TryParameterlessException(op.Target, out var exceptionType))
            {
                stmts.Add(new ThrowStmt(exceptionType, "the helper builds this exception from its type name and does not return") { Addresses = [op.Address] });
                EvidenceNotes.Add($"0x{op.Address:X}: call target 0x{op.Target:X} raises {exceptionType}. The type name is a read-only string reached before __cxa_throw, and the call takes no exception object.");
                return;
            }
            if (TryRaise(op, st, stmts))
                return;
            if (_req.RuntimeHelpers is not null && _req.RuntimeHelpers.TryGetValue(op.Target, out var helper) && helper.Kind == RuntimeHelperKind.RaiseNullReferenceException && RuntimeHelperInference.IsNullCheck(_fn, op.Address))
            {
                stmts.Add(new ThrowStmt("System.NullReferenceException", helper.Evidence) { Addresses = [op.Address] });
                EvidenceNotes.Add($"0x{op.Address:X}: call to 0x{op.Target:X} raises NullReferenceException ({helper.Evidence}).");
                return;
            }
            if (_req.CallTargets is not null && _req.CallTargets.ContainsKey(op.Target))
            {
                var callee = _req.CallTargets[op.Target];
                var call = BuildCall(callee, op.Target, st, op.Address, stmts);
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
                    LiftPair(op, st, stmts);
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
                case A64Kind.VectorMov:
                {
                    if (st.V[op.Rn] is not { } source)
                        throw new LiftException($"0x{op.Address:X}: v{op.Rn} has no lifted value to copy.");
                    st.V[op.Rd] = new Slot { Value = source.Value, Width = source.Width };
                    return;
                }
                case A64Kind.FmovFromGeneral:
                    st.V[op.Rd] = new Slot { Value = BitsToFloat(op.Rn == 31 ? new ConstExpr(0, op.FloatDouble ? PrimitiveKind.Int64 : PrimitiveKind.Int32) : Read(op.Rn, op.FloatDouble, st, op.Address), op.FloatDouble), Width = op.FloatDouble ? 64 : 32 };
                    return;
                case A64Kind.FmovToGeneral:
                    Write(st, op.Rd, w, FloatToBits(ReadFloat(op.Rn, op.FloatDouble, st, op.Address), op.FloatDouble));
                    return;
                case A64Kind.Fsqrt:
                {
                    var ft = op.FloatDouble ? PrimitiveKind.Double : PrimitiveKind.Single;
                    var call = new CallExpr("Sqrt", [ReadFloat(op.Rn, op.FloatDouble, st, op.Address)], ft) { StaticType = op.FloatDouble ? "System.Math" : "System.MathF" };
                    st.V[op.Rd] = new Slot { Value = call, Width = op.FloatDouble ? 64 : 32 };
                    return;
                }
                case A64Kind.Fmin:
                case A64Kind.Fmax:
                case A64Kind.FminNum:
                case A64Kind.FmaxNum:
                {
                    var left = ReadFloat(op.Rn, op.FloatDouble, st, op.Address);
                    var right = ReadFloat(op.Rm, op.FloatDouble, st, op.Address);
                    st.V[op.Rd] = new Slot
                    {
                        Value = new FloatExtremumExpr(op.Kind is A64Kind.Fmin or A64Kind.FminNum, op.Kind is A64Kind.FminNum or A64Kind.FmaxNum, left, right),
                        Width = op.FloatDouble ? 64 : 32
                    };
                    return;
                }
                case A64Kind.SimdScvtf:
                case A64Kind.SimdUcvtf:
                {
                    if (st.V[op.Rn] is not { } lane)
                        throw new LiftException($"0x{op.Address:X}: v{op.Rn} has no integer bits to convert.");
                    var src = lane.Value;
                    if (op.Kind == A64Kind.SimdUcvtf && src.Type is PrimitiveKind.Int32 or PrimitiveKind.Int64)
                        src = new CastExpr(src, src.Type == PrimitiveKind.Int32 ? PrimitiveKind.UInt32 : PrimitiveKind.UInt64);
                    var ft = op.FloatDouble ? PrimitiveKind.Double : PrimitiveKind.Single;
                    st.V[op.Rd] = new Slot { Value = new CastExpr(src, ft), Width = op.FloatDouble ? 64 : 32 };
                    EvidenceNotes.Add($"0x{op.Address:X}: SIMD {(op.Kind == A64Kind.SimdUcvtf ? "unsigned" : "signed")} convert of the integer bits in v{op.Rn}.");
                    return;
                }
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
                var at = global.Address + (ulong)offset;
                if (op.Kind == A64Kind.Ldr && op.Size == 8 && !op.SignedLoad && _req.ResolveMetadataSlot?.Invoke(at) is MetadataSlot slot)
                {
                    Expr resolved = SlotExpr(slot, slot.ValueAtAddress);
                    Write(st, op.Rt, 64, resolved);
                    EvidenceNotes.Add($"0x{op.Address:X}: address 0x{at:X} is a metadata {slot.Kind} slot ({slot.Display}).");
                    return;
                }
                var staticField = _req.Contract.Fields.FirstOrDefault(f => f.IsStatic && f.AbsoluteAddress == at);
                if (staticField is null)
                    throw new LiftException($"0x{op.Address:X}: access to global data at 0x{at:X} (IL2CPP metadata usage, class pointer, or static storage) is not resolved. No pointer was invented.");
                AccessField(op, null, staticField, st, stmts);
                return;
            }
            if (baseExpr is MetadataEntryExpr entry)
            {
                if (op.Kind == A64Kind.Ldr && offset == 0 && op.Size == 8 && !op.SignedLoad)
                {
                    Write(st, op.Rt, 64, SlotExpr(entry.Slot, true));
                    EvidenceNotes.Add($"0x{op.Address:X}: load of resolved metadata {entry.Slot.Kind} {entry.Slot.Display}.");
                    return;
                }
                throw new LiftException($"0x{op.Address:X}: metadata entry {entry.Slot.Display} is not consumed as the resolved value. No field was invented.");
            }
            if (baseExpr is MetadataValueExpr usage)
            {
                if (usage.Slot.Kind == MetadataSlotKind.Type
                    && Il2CppClassLayout.TryGet(_req.MetadataVersion, out var staticFields, out _)
                    && op.Kind == A64Kind.Ldr && op.Size == 8 && !op.SignedLoad && offset == staticFields)
                {
                    if (usage.Slot.DefinitionIndex is null)
                        throw new LiftException($"0x{op.Address:X}: static fields of {usage.Slot.Display} are unresolved because the type slot has no definition index.");
                    Write(st, op.Rt, 64, new ClassStaticBaseExpr(usage.Slot));
                    EvidenceNotes.Add($"0x{op.Address:X}: load of Il2CppClass.static_fields at +0x{staticFields:X} for {usage.Slot.Display}.");
                    return;
                }
                throw new LiftException($"0x{op.Address:X}: resolved metadata {usage.Slot.Kind} {usage.Slot.Display} is not a lifted object. No pointer was invented.");
            }
            if (baseExpr is ClassStaticBaseExpr staticBase)
            {
                var staticFieldsOfType = staticBase.Slot.DefinitionIndex is int staticId && _req.TypeFieldsById is not null && _req.TypeFieldsById.TryGetValue(staticId, out var listed) ? listed : [];
                var staticField = staticFieldsOfType.FirstOrDefault(f => f.IsStatic && f.StaticOffset == offset);
                if (staticField is null)
                    throw new LiftException($"0x{op.Address:X}: no static field of {staticBase.Slot.Display} at static offset 0x{offset:X}. No field was invented.");
                AccessField(op, null, staticField, st, stmts);
                EvidenceNotes.Add($"0x{op.Address:X}: static field {staticField.OwnerType}::{staticField.Name} at static offset 0x{offset:X}.");
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
            {
                var note = baseExpr.RefTypeId is int noted && _req.LayoutNotes is not null && _req.LayoutNotes.TryGetValue(noted, out var text) ? " " + text : "";
                var message = note.Length == 0
                    ? $"0x{op.Address:X}: no field of {baseExpr.RefType ?? "this reference"} at offset 0x{offset:X} is known. No pointer was invented."
                    : $"0x{op.Address:X}: offset 0x{offset:X} of {baseExpr.RefType ?? "this reference"} is outside the lifter subset.{note}";
                throw new LiftException(message);
            }
            AccessField(op, baseExpr, field, st, stmts);
        }

        private IReadOnlyList<FieldContract> FieldsOf(Expr reference)
        {
            if (reference is ThisExpr || TypeIdentity.Same(reference.RefTypeId, reference.RefType, _selfTypeId, _selfType))
                return _req.Contract.Fields;
            if (reference.RefTypeId is int id && _req.TypeFieldsById is not null && _req.TypeFieldsById.TryGetValue(id, out var byId))
                return byId;
            if (reference.RefTypeId is null && reference.RefType is not null && _req.TypeFields is not null && _req.TypeFields.TryGetValue(reference.RefType, out var byName))
                return byName;
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
            {
                if (isFloat && !op.Vector && op.Kind == A64Kind.Str)
                {
                    var bits = BitPattern(op.Rt, size, st, op.Address);
                    var value = ReinterpretFloat(bits, field.Type == PrimitiveKind.Double, op.Address);
                    stmts.Add(new StoreFieldStmt(instance, field.Name, value) { Addresses = [op.Address], StaticOwner = instance is null ? field.OwnerType : null });
                    EvidenceNotes.Add($"0x{op.Address:X}: a {size * 8}-bit general-register store into {field.Name} is the IEEE bit pattern, not a numeric cast.");
                    return;
                }
                if (!isFloat && op.Vector && op.Kind == A64Kind.Ldr && field.Type is PrimitiveKind.Int32 or PrimitiveKind.UInt32 or PrimitiveKind.Int64 or PrimitiveKind.UInt64)
                {
                    var integerBits = new FieldExpr(instance, field.Name, field.Type) { RefType = field.DeclaredType, RefTypeId = field.DeclaredTypeId, StaticType = instance is null ? field.OwnerType : null };
                    var integerTemp = NewTemp(integerBits, op.Address, stmts);
                    st.V[op.Rt] = new Slot { Value = integerTemp, Width = size * 8 };
                    EvidenceNotes.Add($"0x{op.Address:X}: {field.Name} was loaded through an FP register as integer bits.");
                    return;
                }
                throw new LiftException($"0x{op.Address:X}: register class does not match field {field.Name} ({field.Type}).");
            }
            if (op.Kind == A64Kind.Str)
            {
                Expr value;
                if (op.Vector)
                    value = ReadFloat(op.Rt, field.Type == PrimitiveKind.Double, st, op.Address);
                else if (op.Rt == 31)
                    value = field.Type switch
                    {
                        PrimitiveKind.Reference => new NullExpr(),
                        PrimitiveKind.Boolean => new ConstExpr(0, PrimitiveKind.Boolean),
                        PrimitiveKind.Single => new FloatConstExpr(0, PrimitiveKind.Single),
                        PrimitiveKind.Double => new FloatConstExpr(0, PrimitiveKind.Double),
                        _ => new ConstExpr(0, field.Type)
                    };
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
                stmts.Add(new StoreFieldStmt(instance, field.Name, value) { Addresses = [op.Address], StaticOwner = instance is null ? field.OwnerType : null });
                return;
            }
            var load = new FieldExpr(instance, field.Name, field.Type)
            {
                RefType = field.Type == PrimitiveKind.Reference ? field.DeclaredType : null,
                RefTypeId = field.Type == PrimitiveKind.Reference ? field.DeclaredTypeId : null,
                StaticType = instance is null ? field.OwnerType : null
            };
            var temp = NewTemp(load, op.Address, stmts);
            if (instance is null)
                stmts[^1].StaticOwner = field.OwnerType;
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

        private void LiftPair(A64Op op, State st, List<Stmt> stmts)
        {
            var baseExpr = Read(op.Rn, true, st, op.Address, spAllowed: true);
            if (baseExpr.Type == PrimitiveKind.Reference && op.Index == A64Index.Offset)
            {
                LiftObjectPair(op, baseExpr, st, stmts);
                return;
            }
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

        private void LiftObjectPair(A64Op op, Expr baseExpr, State st, List<Stmt> stmts)
        {
            var fields = FieldsOf(baseExpr);
            var first = fields.FirstOrDefault(f => !f.IsStatic && f.InstanceOffset == op.Imm);
            var second = fields.FirstOrDefault(f => !f.IsStatic && f.InstanceOffset == op.Imm + op.Size);
            if (first is null || second is null)
                throw new LiftException($"0x{op.Address:X}: load/store pair at 0x{op.Imm:X} does not match two fields {op.Size} bytes apart. No field was invented.");
            if (SizeOf(first.Type) != op.Size || SizeOf(second.Type) != op.Size)
                throw new LiftException($"0x{op.Address:X}: load/store pair size {op.Size} does not match {first.Name} and {second.Name}.");
            var floatPair = first.Type is PrimitiveKind.Single or PrimitiveKind.Double;
            if (floatPair != op.Vector || first.Type != second.Type)
                throw new LiftException($"0x{op.Address:X}: load/store pair register class does not match {first.Name} ({first.Type}) and {second.Name} ({second.Type}).");
            if (op.Kind == A64Kind.Stp)
            {
                AccessField(CloneAs(op, A64Kind.Str, op.Rt, op.Imm), baseExpr, first, st, stmts);
                AccessField(CloneAs(op, A64Kind.Str, op.Rt2, op.Imm + op.Size), baseExpr, second, st, stmts);
            }
            else
            {
                AccessField(CloneAs(op, A64Kind.Ldr, op.Rt, op.Imm), baseExpr, first, st, stmts);
                AccessField(CloneAs(op, A64Kind.Ldr, op.Rt2, op.Imm + op.Size), baseExpr, second, st, stmts);
            }
            EvidenceNotes.Add($"0x{op.Address:X}: pair access split into {first.Name} at 0x{op.Imm:X} and {second.Name} at 0x{op.Imm + op.Size:X}.");
        }

        private static A64Op CloneAs(A64Op op, A64Kind kind, int rt, long offset) => new()
        {
            Word = op.Word,
            Address = op.Address,
            Kind = kind,
            Is64 = op.Size == 8,
            Rt = rt,
            Rn = op.Rn,
            Size = op.Size,
            Vector = op.Vector,
            Imm = offset,
            Index = A64Index.Offset
        };

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
            if (!(_req.CallTargets?.ContainsKey(op.Target) ?? false) && TryRuntimeThunk(op, st, stmts))
                return;
            if (TryRaise(op, st, stmts))
                return;
            var callee = ResolveCallee(op.Target, op.Address, out var chain);
            if (chain is not null)
                EvidenceNotes.Add($"0x{op.Address:X}: call resolved through thunk chain {chain}.");
            var call = BuildCall(callee, op.Target, st, op.Address, stmts);
            callee = _builtCallee;
            for (var r = 0; r <= 18; r++)
                st.X[r] = null;
            st.X[30] = new Slot { Value = new OpaqueExpr("clobbered link register"), Width = 64 };
            for (var r = 0; r <= 7; r++)
                st.V[r] = null;
            for (var r = 16; r <= 31; r++)
                st.V[r] = null;
            st.Flags = null;
            if (call is null)
                return;
            if (call is CtorInitializerStmt init)
            {
                stmts.Add(init);
                return;
            }
            var expr = (CallExpr)call;
            if (callee.ReturnType == PrimitiveKind.Void)
            {
                stmts.Add(new ExpressionStmt(expr) { Addresses = [op.Address] });
                if (callee.IsStatic)
                    stmts[^1].StaticOwner = Qualified(callee.Namespace, callee.DeclaringType);
                return;
            }
            var temp = NewTemp(expr, op.Address, stmts);
            if (callee.IsStatic)
                stmts[^1].StaticOwner = Qualified(callee.Namespace, callee.DeclaringType);
            if (HfaType.TryComponents(callee.DeclaredReturnType, out var lanes))
            {
                for (var lane = 0; lane < lanes.Length; lane++)
                    st.V[lane] = new Slot { Value = new FieldExpr(temp, lanes[lane], PrimitiveKind.Single), Width = 32 };
                EvidenceNotes.Add($"0x{op.Address:X}: {callee.DeclaredReturnType} is returned in s0-s{lanes.Length - 1} (AAPCS64 HFA).");
            }
            else if (callee.ReturnType is PrimitiveKind.Single or PrimitiveKind.Double)
                st.V[0] = new Slot { Value = temp, Width = callee.ReturnType == PrimitiveKind.Double ? 64 : 32 };
            else
                st.X[0] = new Slot { Value = temp, Width = WidthOf(callee.ReturnType) };
        }

        private object BuildCall(MethodContract callee, ulong target, State st, ulong site, List<Stmt> stmts)
        {
            callee = SpecializeGeneric(callee, st, site);
            _builtCallee = callee;
            if (TryDelegateConstruction(callee, st, site, stmts))
                return null!;
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
                if (HfaType.TryComponents(p.DeclaredType, out var lanes))
                {
                    var parts = new List<Expr>();
                    foreach (var _ in lanes)
                    {
                        if (nsrn >= 8)
                            throw new LiftException($"0x{site:X}: stack-passed arguments are not lifted.");
                        parts.Add(ReadFloat(nsrn++, false, st, site));
                    }
                    args.Add(new NewExpr(p.DeclaredType!, parts) { RefType = p.DeclaredType });
                }
                else if (p.Type is PrimitiveKind.Single or PrimitiveKind.Double)
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
            var sameType = TypeIdentity.Same(callee.DeclaringTypeId, calleeType, _selfTypeId, _selfType);
            if (callee.Name == ".ctor" && receiver is ThisExpr && _req.Contract.Name == ".ctor")
            {
                var isBase = !sameType;
                EvidenceNotes.Add($"0x{site:X}: call to {calleeType}::.ctor on this inside a constructor is the {(isBase ? "base" : "this")} constructor initializer.");
                return new CtorInitializerStmt(isBase, args) { Addresses = [site] };
            }
            if (callee.Name == ".ctor" && callee.Namespace == "System" && callee.DeclaringType == "Object" && args.Count == 0 && receiver is not ThisExpr)
            {
                EvidenceNotes.Add($"0x{site:X}: System.Object.ctor on an allocated instance adds no field writes. It was not rewritten as new T().");
                return null!;
            }
            if (callee.Name == ".ctor" && receiver is LocalExpr allocated && TryRewriteAllocation(stmts, allocated.Name, calleeType, callee.DeclaringTypeId, args))
            {
                EvidenceNotes.Add($"0x{site:X}: {calleeType} was allocated and then its constructor was called on that object. The pair is new {calleeType}(...).");
                return null!;
            }
            if (callee.Name is ".ctor" or ".cctor")
                throw new LiftException($"0x{site:X}: a direct constructor call outside an object-creation pattern is not lifted.");
            var baseCall = receiver is ThisExpr && (callee.IsVirtual || callee.IsAbstract) && !sameType;
            if (baseCall)
                EvidenceNotes.Add($"0x{site:X}: direct call on this to virtual {calleeType}::{callee.Name} is emitted as base.{callee.Name}.");
            return new CallExpr(callee.Name, args, callee.ReturnType)
            {
                Receiver = receiver,
                StaticType = callee.IsStatic ? calleeType : null,
                Target = target,
                BaseCall = baseCall,
                RefType = callee.ReturnType == PrimitiveKind.Reference ? callee.DeclaredReturnType : null,
                RefTypeId = callee.ReturnType == PrimitiveKind.Reference ? callee.DeclaredReturnTypeId : null,
                TypeArguments = _typeArguments
            };
        }

        private string? _typeArguments;
        private MethodContract _builtCallee = null!;

        private MethodContract SpecializeGeneric(MethodContract callee, State st, ulong site)
        {
            _typeArguments = null;
            MetadataValueExpr? slot = null;
            for (var register = 0; register <= 7; register++)
            {
                if (st.X[register]?.Value is MetadataValueExpr { Slot.Kind: MetadataSlotKind.Method } value
                    && value.Slot.Display.Contains(callee.Name, StringComparison.Ordinal))
                    slot = value;
            }
            if (slot?.Slot.GenericArguments is not { Count: > 0 } arguments)
                return callee;
            if (callee.UnsupportedReason is not null && !callee.UnsupportedReason.StartsWith("return generic parameter", StringComparison.Ordinal))
                return callee;
            if (callee.Parameters.Any(p => p.DeclaredType == "T" || (p.DeclaredType?.Contains("generic parameter", StringComparison.Ordinal) ?? false)))
                return callee;
            _typeArguments = string.Join(", ", arguments.Select(CSharpEmitter.FormatDeclaredType));
            var substituted = arguments.Count == 1 ? arguments[0] : callee.DeclaredReturnType;
            EvidenceNotes.Add($"0x{site:X}: method slot {slot.Slot.Display} supplies <{_typeArguments}>. The hidden MethodInfo argument is not emitted.");
            return new MethodContract
            {
                Name = callee.Name,
                Namespace = callee.Namespace,
                DeclaringType = callee.DeclaringType,
                DeclaringTypeId = callee.DeclaringTypeId,
                Assembly = callee.Assembly,
                IsStatic = callee.IsStatic,
                IsVirtual = callee.IsVirtual,
                IsAbstract = callee.IsAbstract,
                Access = callee.Access,
                ReturnType = PrimitiveKind.Reference,
                DeclaredReturnType = substituted,
                Parameters = callee.Parameters,
                Fields = callee.Fields
            };
        }

        private bool TryDelegateConstruction(MethodContract callee, State st, ulong site, List<Stmt> stmts)
        {
            if (callee.Name != ".ctor" || callee.Parameters.Count != 2)
                return false;
            if (callee.Parameters[1].DeclaredType != "System.IntPtr" || callee.Parameters[0].Type != PrimitiveKind.Reference)
                return false;
            if (st.X[0]?.Value is not LocalExpr local)
                return false;
            if (st.X[1]?.Value is not { Type: PrimitiveKind.Reference } target)
                return false;
            if (st.X[2]?.Value is not MetadataValueExpr { Slot.Kind: MetadataSlotKind.Method, Slot.MemberName: { Length: > 0 } member })
                return false;
            var calleeType = Qualified(callee.Namespace, callee.DeclaringType);
            if (!TryRewriteAllocation(stmts, local.Name, calleeType, callee.DeclaringTypeId, [new MethodGroupExpr(target, member)]))
                return false;
            EvidenceNotes.Add($"0x{site:X}: {calleeType}..ctor(object, System.IntPtr) receives the method {member}. IL2CPP passes the method pointer in the IntPtr; the C# form is new {calleeType}({member}).");
            return true;
        }

        private static bool TryRewriteAllocation(List<Stmt> stmts, string local, string typeName, int? typeId, IReadOnlyList<Expr> args)
        {
            for (var i = stmts.Count - 1; i >= 0; i--)
            {
                if (stmts[i] is not DeclareStmt declare || declare.Local != local)
                    continue;
                if (declare.Value is not CastExpr { Value: CallExpr { Name: "GetUninitializedObject" }, RefType: { } allocated })
                    return false;
                if (!string.Equals(allocated, typeName, StringComparison.Ordinal))
                    return false;
                var created = new NewExpr(typeName, args) { RefType = typeName, RefTypeId = typeId };
                stmts[i] = new DeclareStmt(declare.Local, declare.Type, created) { Addresses = declare.Addresses, TypeName = declare.TypeName };
                return true;
            }
            return false;
        }

        /// <summary>
        /// A parameterless IL2CPP raise helper: a straight path loads the namespace and exception
        /// name from read-only data and branches to a function that reaches <c>__cxa_throw</c>.
        /// The caller's registers are not the exception object.
        /// </summary>
        private bool TryParameterlessException(ulong target, out string typeName)
        {
            typeName = "";
            if (_req.Space is null)
                return false;
            string? ns = null;
            string? name = null;
            ulong page1 = 0;
            ulong page2 = 0;
            var seen = new HashSet<ulong>();
            var found = false;
            var usesArgument = false;
            Walk(target, 6, 24, true);
            if (usesArgument)
                return false;
            if (!found || ns != "System" || name is null || name.Length == 0 || name.Length > 80 || !name.EndsWith("Exception", StringComparison.Ordinal) || name.Any(ch => ch is < 'A' or > 'Z' && ch is < 'a' or > 'z'))
                return false;
            typeName = "System." + name;
            return true;

            void Walk(ulong address, int depth, int steps, bool entry)
            {
                if (found || usesArgument || depth < 0 || !seen.Add(address))
                    return;
                for (var i = 0; i < steps && !found && !usesArgument; i++)
                {
                    var word = _req.Space?.ReadWord(address);
                    if (word is null)
                        return;
                    var op = A64Decoder.Decode(word.Value, address);
                    if (entry && ReadsIncomingX0(op))
                    {
                        usesArgument = true;
                        return;
                    }
                    if (op.Kind == A64Kind.Adrp && op.Rd == 1)
                        page1 = op.Target;
                    else if (op.Kind == A64Kind.Adrp && op.Rd == 2)
                        page2 = op.Target;
                    else if (op.Kind == A64Kind.Add && op.HasImm && op.Rd == op.Rn && op.Rd == 1 && page1 != 0 && ns is null)
                        ns = ReadCString(page1 + (ulong)op.Imm);
                    else if (op.Kind == A64Kind.Add && op.HasImm && op.Rd == op.Rn && op.Rd == 2 && page2 != 0 && name is null)
                        name = ReadCString(page2 + (ulong)op.Imm);
                    if (op.Kind is A64Kind.B or A64Kind.Bl && ns == "System" && name is not null && name.EndsWith("Exception", StringComparison.Ordinal) && ReachesCxaThrow(op.Target))
                    {
                        found = true;
                        return;
                    }
                    if (op.Kind == A64Kind.B)
                    {
                        Walk(op.Target, depth - 1, steps, false);
                        return;
                    }
                    if (op.Kind == A64Kind.Bl)
                    {
                        var savedPage1 = page1;
                        var savedPage2 = page2;
                        Walk(op.Target, depth - 1, steps, false);
                        if (found || usesArgument)
                            return;
                        page1 = savedPage1;
                        page2 = savedPage2;
                        address += 4;
                        continue;
                    }
                    if (op.IsConditionalBranch || op.Kind is A64Kind.Ret or A64Kind.Trap)
                        return;
                    address += 4;
                }
            }
        }

        private static bool ReadsIncomingX0(A64Op op)
        {
            if (op.Kind is A64Kind.Ldr or A64Kind.Ldp or A64Kind.Str or A64Kind.Stp && op.Rn == 0)
                return true;
            if (op.Kind is A64Kind.Str or A64Kind.Stp && (op.Rt == 0 || op.Kind == A64Kind.Stp && op.Rt2 == 0))
                return true;
            if (op.Kind is A64Kind.Add or A64Kind.Sub && (op.Rn == 0 || !op.HasImm && op.Rm == 0))
                return true;
            if (op.Kind is A64Kind.Orr or A64Kind.And or A64Kind.Eor && (op.Rm == 0 || op.Rn == 0))
                return true;
            return op.Kind is A64Kind.Cbz or A64Kind.Cbnz or A64Kind.Tbz or A64Kind.Tbnz && op.Rd == 0;
        }

        private string? ReadCString(ulong address)
        {
            if (_req.Space is null)
                return null;
            var chars = new List<byte>(64);
            for (var i = 0; i < 80; i++)
            {
                var b = _req.Space.ReadMapped(address + (ulong)i);
                if (b is null)
                    return null;
                if (b.Value == 0)
                    return chars.Count == 0 ? null : System.Text.Encoding.ASCII.GetString(chars.ToArray());
                if (b.Value is < 32 or > 126)
                    return null;
                chars.Add(b.Value);
            }
            return null;
        }

        private bool TryRaise(A64Op op, State st, List<Stmt> stmts)
        {
            if (_req.CallTargets?.ContainsKey(op.Target) == true || !ReachesCxaThrow(op.Target))
                return false;
            var value = st.X[0]?.Value;
            if (value is null || value.Type != PrimitiveKind.Reference || string.IsNullOrWhiteSpace(value.RefType))
                return false;
            stmts.Add(new ThrowStmt(value.RefType, "helper reaches __cxa_throw") { Addresses = [op.Address], Value = value });
            EvidenceNotes.Add($"0x{op.Address:X}: call target 0x{op.Target:X} reaches __cxa_throw with {value.RefType} in x0. The call is throw of that object. The MethodInfo argument is the IL2CPP exception frame and is not emitted.");
            ClobberCall(st);
            return true;
        }

        private bool ReachesCxaThrow(ulong target)
        {
            if (_req.CodeSymbols is null || _req.Space is null)
                return false;
            return Walk(target, 2, 24);

            bool Walk(ulong address, int depth, int steps)
            {
                for (var i = 0; i < steps; i++)
                {
                    if (_req.CodeSymbols.TryGetValue(address, out var here) && here == "__cxa_throw")
                        return true;
                    var word = _req.Space.ReadWord(address);
                    if (word is null)
                        return false;
                    var decoded = A64Decoder.Decode(word.Value, address);
                    if (decoded.Kind is A64Kind.Bl or A64Kind.B)
                    {
                        if (_req.CodeSymbols.TryGetValue(decoded.Target, out var called) && called == "__cxa_throw")
                            return true;
                        if (depth > 0 && Walk(decoded.Target, depth - 1, steps))
                            return true;
                        if (decoded.Kind == A64Kind.B)
                            return false;
                    }
                    else if (decoded.Kind is A64Kind.Ret or A64Kind.Trap || decoded.IsConditionalBranch)
                        return false;
                    address += 4;
                }
                return false;
            }
        }

        private bool TryRuntimeThunk(A64Op op, State st, List<Stmt> stmts)
        {
            if (_req.Space?.ReadWord(op.Target) is not uint word || A64Decoder.Decode(word, op.Target).Kind != A64Kind.B)
                return false;
            var x0 = st.X[0]?.Value;
            if (x0 is MetadataValueExpr { Slot.Kind: MetadataSlotKind.Type } boxed && st.X[1]?.Value is StackAddrExpr stack)
            {
                if (!st.Stack.TryGetValue(stack.Offset, out var stored))
                    throw new LiftException($"0x{op.Address:X}: box of {boxed.Slot.Display} reads a stack slot this method did not write.");
                var temp = NewTemp(new BoxExpr(stored.Value, boxed.Slot.Display), op.Address, stmts);
                ClobberCall(st);
                st.X[0] = new Slot { Value = temp, Width = 64 };
                EvidenceNotes.Add($"0x{op.Address:X}: box({boxed.Slot.Display}) of the value at sp+0x{stack.Offset:X}. The helper at 0x{op.Target:X} is one branch.");
                return true;
            }
            if (x0 is MetadataEntryExpr entry && OtherArgsEmpty(st))
            {
                var resolved = SlotExpr(entry.Slot, true);
                ClobberCall(st);
                st.X[0] = new Slot { Value = resolved, Width = 64 };
                EvidenceNotes.Add($"0x{op.Address:X}: metadata-init helper at 0x{op.Target:X} (one branch) returns the resolved {entry.Slot.Kind} {entry.Slot.Display}.");
                return true;
            }
            if (x0 is MetadataValueExpr { Slot.Kind: MetadataSlotKind.Type } type && OtherArgsEmpty(st))
            {
                if (UsesResult(op.Address))
                {
                    var allocated = new CastExpr(
                        new CallExpr("GetUninitializedObject", [new TypeOfExpr(type.Slot.Display)], PrimitiveKind.Reference)
                        { StaticType = "System.Runtime.CompilerServices.RuntimeHelpers" },
                        PrimitiveKind.Reference)
                    { RefType = type.Slot.Display, RefTypeId = type.Slot.DefinitionIndex };
                    var temp = NewTemp(allocated, op.Address, stmts);
                    ClobberCall(st);
                    st.X[0] = new Slot { Value = temp, Width = 64 };
                    EvidenceNotes.Add($"0x{op.Address:X}: allocation of {type.Slot.Display}. The helper at 0x{op.Target:X} is one branch. new {type.Slot.Display}() was not emitted; the following constructor sequence is not yet proven equal to that type's constructor.");
                    return true;
                }
                stmts.Add(new ClassInitStmt(type.Slot.Display) { Addresses = [op.Address] });
                ClobberCall(st);
                EvidenceNotes.Add($"0x{op.Address:X}: class-init helper at 0x{op.Target:X} for {type.Slot.Display}. Its result is unused.");
                return true;
            }
            return false;
        }

        private static bool OtherArgsEmpty(State st)
        {
            for (var register = 1; register <= 7; register++)
            {
                if (st.X[register]?.Value is null or OpaqueExpr or ConstExpr { Value: 0 })
                    continue;
                return false;
            }
            return true;
        }

        private bool UsesResult(ulong address)
        {
            for (var at = address + 4; _fn.Instructions.TryGetValue(at, out var next); at += 4)
            {
                if (next.Kind == A64Kind.Ret)
                    return _req.Contract.ReturnType != PrimitiveKind.Void;
                if (UsesGpr(next, 0))
                    return true;
                if (A64Effects.WritesGpr(next, 0) || next.Kind is A64Kind.B or A64Kind.BCond or A64Kind.Cbz or A64Kind.Cbnz or A64Kind.Tbz or A64Kind.Tbnz)
                    return false;
            }
            return false;
        }

        private static bool UsesGpr(A64Op op, int register)
        {
            if (op.Kind is A64Kind.Ldr or A64Kind.Str or A64Kind.Ldp or A64Kind.Stp)
            {
                if (op.Rn == register)
                    return true;
                if (op.Kind is A64Kind.Str or A64Kind.Stp && op.Rt == register)
                    return true;
                if (op.Kind == A64Kind.Stp && op.Rt2 == register)
                    return true;
            }
            if (op.Kind is A64Kind.Bl or A64Kind.Blr)
                return register <= 7;
            if (!op.HasImm && op.Rm == register && op.Kind is not (A64Kind.Str or A64Kind.Stp or A64Kind.Ldr or A64Kind.Ldp))
                return true;
            if (op.Rn == register && op.Kind is not (A64Kind.Ldr or A64Kind.Str or A64Kind.Ldp or A64Kind.Stp) && !(op.Kind == A64Kind.Orr && op.Rn == 31))
                return true;
            return false;
        }

        private static void ClobberCall(State st)
        {
            for (var register = 0; register <= 18; register++)
                st.X[register] = null;
            st.X[30] = new Slot { Value = new OpaqueExpr("clobbered link register"), Width = 64 };
            for (var register = 0; register <= 7; register++)
                st.V[register] = null;
            for (var register = 16; register <= 31; register++)
                st.V[register] = null;
            st.Flags = null;
        }

        private static Expr SlotExpr(MetadataSlot slot, bool resolvedValue)
        {
            if (!resolvedValue)
                return new MetadataEntryExpr(slot);
            if (slot.Kind == MetadataSlotKind.StringLiteral && slot.LiteralText is not null)
                return new StringConstExpr(slot.LiteralText);
            return new MetadataValueExpr(slot);
        }

        private Expr BitPattern(int rt, int size, State st, ulong address) =>
            rt == 31
                ? new ConstExpr(0, size == 8 ? PrimitiveKind.Int64 : PrimitiveKind.Int32)
                : Read(rt, size == 8, st, address);

        private static Expr ReinterpretFloat(Expr bits, bool isDouble, ulong address)
        {
            _ = address;
            return BitsToFloat(bits, isDouble);
        }

        private static Expr BitsToFloat(Expr bits, bool isDouble)
        {
            if (bits is ConstExpr constant)
            {
                if (isDouble)
                    return new FloatConstExpr(BitConverter.Int64BitsToDouble(constant.Value), PrimitiveKind.Double);
                return new FloatConstExpr(BitConverter.Int32BitsToSingle(unchecked((int)(uint)constant.Value)), PrimitiveKind.Single);
            }
            var arg = isDouble ? Coerce(bits, PrimitiveKind.Int64) : Coerce(bits, PrimitiveKind.Int32);
            return new CallExpr(isDouble ? "Int64BitsToDouble" : "Int32BitsToSingle", [arg], isDouble ? PrimitiveKind.Double : PrimitiveKind.Single)
            {
                StaticType = "System.BitConverter"
            };
        }

        private static Expr FloatToBits(Expr value, bool isDouble)
        {
            if (value is FloatConstExpr constant)
            {
                if (isDouble)
                    return new ConstExpr(BitConverter.DoubleToInt64Bits(constant.Value), PrimitiveKind.Int64);
                return new ConstExpr(unchecked((uint)BitConverter.SingleToInt32Bits((float)constant.Value)), PrimitiveKind.Int32);
            }
            return new CallExpr(isDouble ? "DoubleToInt64Bits" : "SingleToInt32Bits", [value], isDouble ? PrimitiveKind.Int64 : PrimitiveKind.Int32)
            {
                StaticType = "System.BitConverter"
            };
        }

        private LocalExpr NewTemp(Expr value, ulong address, List<Stmt> stmts)
        {
            var name = "t" + (++_temp);
            stmts.Add(new DeclareStmt(name, value.Type, value) { Addresses = [address], TypeName = value.RefType });
            return new LocalExpr(name, value.Type) { RefType = value.RefType, RefTypeId = value.RefTypeId };
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
