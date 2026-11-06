using Disarm;
using Disarm.InternalDisassembly;

namespace Il2CppRecover.Recovery;

/// <summary>
/// Lifts a narrow subset of AArch64 into explicit statements.
/// Instructions outside the subset produce an incomplete status. Nothing is
/// replaced with a guessed return value.
/// </summary>
public static class Arm64Recoverer
{
    public static IReadOnlyList<NativeInstruction> Disassemble(byte[] code, ulong address)
    {
        if (code.Length == 0 || code.Length % 4 != 0)
            return [];
        try
        {
            var instructions = Disassembler.Disassemble(code, address, new Disassembler.Options(true, true, false)).ToList();
            return Render(code, instructions);
        }
        catch (Exception)
        {
            return [];
        }
    }

    /// <summary>
    /// Discovers the function by recursive descent and lifts it. The legacy shape matcher runs
    /// only when the graph contains a loop, and it keeps the next-address boundary rule.
    /// A body is recovered only when discovery reports closed control flow.
    /// </summary>
    public static MethodBodyResult Recover(RecoveryRequest request)
    {
        if (request.Code.Length == 0)
            return Fail(request, "The method slice is empty.", []);
        if (request.Code.Length % 4 != 0)
            return Fail(request, "The ARM64 slice length is not a multiple of 4.", []);
        var rendered = Disassemble(request.Code, request.VirtualAddress).ToList();
        if (request.Contract.IsGeneric)
            return Incomplete(request, MethodRecoveryStatus.Unsupported, rendered, ["Generic and shared-generic method bodies are not recovered in this milestone."]);

        var space = request.Space ?? CodeSpace.ForSlice(request.Code, request.VirtualAddress);
        var fn = FunctionDiscovery.Discover(space, request.VirtualAddress);
        var listing = ListingFor(fn, rendered);
        string? thunkChain = null;
        if (fn.Instructions.Count == 1 && fn.TailCalls.Count == 1)
        {
            var only = fn.Instructions.Values.First();
            thunkChain = "0x" + only.Address.ToString("X") + " -> 0x" + only.Target.ToString("X");
            if (request.CallTargets is not null && !request.CallTargets.ContainsKey(only.Target))
            {
                var resolution = FunctionDiscovery.ResolveThunk(space, only.Target, a => request.CallTargets.ContainsKey(a));
                thunkChain = "0x" + only.Address.ToString("X") + " -> " + resolution.Describe();
            }
        }

        var outcome = StructuredLifter.Lift(request, fn);
        if (outcome.HasLoop)
        {
            var legacy = LegacyRecover(request);
            var notes = legacy.Diagnostics.ToList();
            notes.Insert(0, "The control-flow graph has a loop; the structured lifter does not structure loops, so the legacy single-loop matcher was tried.");
            return new MethodBodyResult
            {
                Status = legacy.Status,
                Diagnostics = notes,
                Instructions = legacy.Instructions,
                CSharp = legacy.CSharp,
                CSharpIsPlaceholder = legacy.CSharpIsPlaceholder,
                CandidateCSharp = legacy.CandidateCSharp,
                Provenance = legacy.Provenance,
                Statements = legacy.Statements,
                Boundary = request.Boundary,
                Engine = "legacy-shape"
            };
        }

        var diagnostics = new List<string>();
        diagnostics.AddRange(fn.Issues);
        diagnostics.AddRange(outcome.Diagnostics);
        if (outcome.Statements is null || !fn.ControlFlowClosed)
        {
            if (diagnostics.Count == 0)
                diagnostics.Add("The structured lifter produced no body.");
            if (thunkChain is not null)
                diagnostics.Insert(0, "Kind: thunk " + thunkChain);
            var status = diagnostics.Any(d => d.Contains("failed", StringComparison.OrdinalIgnoreCase))
                ? MethodRecoveryStatus.Failed
                : MethodRecoveryStatus.Unsupported;
            return new MethodBodyResult
            {
                Status = status,
                Diagnostics = diagnostics,
                Instructions = listing,
                CSharp = CSharpEmitter.Placeholder(status, diagnostics),
                CSharpIsPlaceholder = true,
                Boundary = fn.ControlFlowClosed ? BoundaryEvidence.ControlFlowClosed : request.Boundary,
                Engine = "structured",
                ThunkChain = thunkChain
            };
        }

        var evidence = outcome.Evidence.Concat(fn.Evidence).ToList();
        if (thunkChain is not null)
            evidence.Insert(0, "Direct thunk: " + thunkChain + ".");
        evidence.Insert(0, "Boundary: control flow closed. Every reachable path ends in RET, a tail call to a known method entry, or a call with noreturn evidence.");
        string? body = null;
        string? emitError = null;
        try
        {
            body = CSharpEmitter.EmitBody(outcome.Statements);
        }
        catch (UnemittableException ex)
        {
            emitError = ex.Message;
        }

        if (outcome.CandidateOnly || emitError is not null)
        {
            var notes = diagnostics.ToList();
            if (emitError is not null)
                notes.Add("No verified C# was emitted: " + emitError + ".");
            notes.AddRange(evidence.Select(e => "Evidence: " + e));
            return new MethodBodyResult
            {
                Status = MethodRecoveryStatus.PartiallyRecovered,
                Diagnostics = notes,
                Instructions = listing,
                CSharp = CSharpEmitter.Placeholder(MethodRecoveryStatus.PartiallyRecovered, notes),
                CSharpIsPlaceholder = true,
                CandidateCSharp = CSharpEmitter.EmitCandidateBody(outcome.Statements),
                Provenance = Provenance(outcome.Statements),
                Statements = outcome.Statements,
                Boundary = BoundaryEvidence.ControlFlowClosed,
                Engine = "structured",
                ThunkChain = thunkChain
            };
        }

        diagnostics.AddRange(evidence.Select(e => "Evidence: " + e));
        return new MethodBodyResult
        {
            Status = MethodRecoveryStatus.RecoveredUnvalidated,
            Diagnostics = diagnostics,
            Instructions = listing,
            CSharp = body,
            CSharpIsPlaceholder = false,
            Provenance = Provenance(outcome.Statements),
            Statements = outcome.Statements,
            Boundary = BoundaryEvidence.ControlFlowClosed,
            Engine = "structured",
            ThunkChain = thunkChain
        };
    }

    private static List<NativeInstruction> ListingFor(DiscoveredFunction fn, List<NativeInstruction> sliceListing)
    {
        if (fn.Instructions.Count == 0)
            return sliceListing;
        var byAddress = sliceListing.ToDictionary(i => i.Address);
        var list = new List<NativeInstruction>();
        foreach (var op in fn.Instructions.Values)
        {
            var key = "0x" + op.Address.ToString("X");
            if (byAddress.TryGetValue(key, out var existing))
            {
                list.Add(existing);
                continue;
            }
            var bytes = BitConverter.GetBytes(op.Word);
            list.Add(new NativeInstruction
            {
                Address = key,
                Bytes = Convert.ToHexString(bytes).ToLowerInvariant(),
                Text = Disassemble(bytes, op.Address).FirstOrDefault()?.Text ?? op.Kind.ToString()
            });
        }
        return list;
    }

    /// <summary>The original shape matcher, used for the single while-loop shape.</summary>
    public static MethodBodyResult LegacyRecover(RecoveryRequest request)
    {
        if (request.Code.Length == 0)
            return Fail(request, "The method slice is empty.", []);
        if (request.Code.Length % 4 != 0)
            return Fail(request, "The ARM64 slice length is not a multiple of 4.", []);

        List<Arm64Instruction> instructions;
        try
        {
            instructions = Disassembler.Disassemble(request.Code, request.VirtualAddress, new Disassembler.Options(true, true, false)).ToList();
        }
        catch (Exception ex)
        {
            return Fail(request, "Disassembly failed: " + ex.Message, []);
        }

        var rendered = Render(request.Code, instructions);
        if (instructions.Count != request.Code.Length / 4)
            return Fail(request, "The disassembler did not consume the whole slice.", rendered);

        var invalidIndex = instructions.FindIndex(i => i.Mnemonic is Arm64Mnemonic.INVALID or Arm64Mnemonic.UNIMPLEMENTED);
        if (invalidIndex >= 0)
        {
            var invalid = instructions[invalidIndex];
            return Fail(request, $"Instruction at 0x{invalid.Address:X} is {invalid.Mnemonic}.", rendered);
        }

        if (request.Contract.IsGeneric)
            return Incomplete(request, MethodRecoveryStatus.Unsupported, rendered, ["Generic and shared-generic method bodies are not recovered in this milestone."]);

        if (request.Contract.ReturnType is PrimitiveKind.Reference)
            return Incomplete(request, MethodRecoveryStatus.Unsupported, rendered, ["Reference returns other than the implemented field and call patterns are not recovered from the signature alone."]);

        if (instructions.Count == 1 && instructions[0].Mnemonic == Arm64Mnemonic.B && instructions[0].MnemonicConditionCode == Arm64ConditionCode.NONE)
            return Incomplete(request, MethodRecoveryStatus.Unsupported, rendered, ["Direct thunks are not expanded."], MethodKindNote: "thunk");

        List<Block> blocks;
        try
        {
            blocks = BuildCfg(instructions);
        }
        catch (Exception ex)
        {
            return Incomplete(request, MethodRecoveryStatus.Failed, rendered, ["Control-flow construction failed: " + ex.Message]);
        }

        var reachable = Reachable(blocks);
        if (reachable.Any(b => b.Indirect || b.External))
        {
            var notes = new List<string>();
            if (reachable.Any(b => b.Indirect))
                notes.Add("The slice contains an indirect branch (BR). Its target is not a constant, so the body was not invented.");
            if (reachable.Any(b => b.External))
                notes.Add("The slice contains a branch whose target is outside the slice. The boundary was not guessed.");
            return Incomplete(request, MethodRecoveryStatus.Unsupported, rendered, notes);
        }

        var lifted = TryLift(request, instructions, blocks, reachable, out var diagnostics);
        if (lifted is null)
            return Incomplete(request, diagnostics.Count == 0 ? MethodRecoveryStatus.Unsupported : StatusFor(diagnostics), rendered, diagnostics);

        var body = CSharpEmitter.EmitBody(lifted);
        var provenance = Provenance(lifted);
        var boundaryNote = request.Boundary == BoundaryEvidence.NextMethodAddressOnly;
        if (boundaryNote)
        {
            var notes = diagnostics.ToList();
            notes.Add("A body was lifted, but the slice was bounded only by the next method address. That boundary is not treated as recovered method logic.");
            return new MethodBodyResult
            {
                Status = MethodRecoveryStatus.PartiallyRecovered,
                Diagnostics = notes,
                Instructions = rendered,
                CSharp = CSharpEmitter.Placeholder(MethodRecoveryStatus.PartiallyRecovered, notes),
                CSharpIsPlaceholder = true,
                CandidateCSharp = body,
                Provenance = provenance,
                Statements = lifted
            };
        }

        return new MethodBodyResult
        {
            Status = MethodRecoveryStatus.RecoveredUnvalidated,
            Diagnostics = diagnostics,
            Instructions = rendered,
            CSharp = body,
            CSharpIsPlaceholder = false,
            Provenance = provenance,
            Statements = lifted
        };
    }

    private static MethodRecoveryStatus StatusFor(List<string> diagnostics) =>
        diagnostics.Any(d => d.Contains("failed", StringComparison.OrdinalIgnoreCase))
            ? MethodRecoveryStatus.Failed
            : MethodRecoveryStatus.Unsupported;

    private static List<Stmt>? TryLift(RecoveryRequest request, List<Arm64Instruction> insns, List<Block> blocks, List<Block> reachable, out List<string> diagnostics)
    {
        diagnostics = [];
        var entry = blocks[0];
        if (!reachable.Contains(entry))
        {
            diagnostics.Add("The entry block is unreachable.");
            return null;
        }

        if (reachable.Count == 1 && EndsWith(insns, entry, Arm64Mnemonic.RET))
            return LiftTerminal(request, insns, entry, Initial(request), diagnostics);

        if (TryMatchIfReturn(insns, reachable, entry, out var thenBlock, out var elseBlock))
            return LiftIfReturn(request, insns, entry, thenBlock!, elseBlock!, diagnostics);

        if (TryMatchWhile(insns, blocks, reachable, entry, out var prelude, out var header, out var body, out var exit))
            return LiftWhile(request, insns, prelude, header!, body!, exit!, diagnostics);

        diagnostics.Add("Control-flow shape is outside the implemented subset: straight-line return, if/else with both sides returning, or a single while-loop.");
        return null;
    }

    private static List<Stmt>? LiftTerminal(RecoveryRequest request, List<Arm64Instruction> insns, Block block, RegState state, List<string> diagnostics)
    {
        var statements = new List<Stmt>();
        if (!LiftRange(request, insns, block.First, block.Last - 1, state, statements, locals: null, diagnostics))
            return null;
        if (!AppendReturn(request, state, statements, insns[block.Last].Address, diagnostics))
            return null;
        return statements;
    }

    private static List<Stmt>? LiftIfReturn(RecoveryRequest request, List<Arm64Instruction> insns, Block header, Block taken, Block fallthrough, List<string> diagnostics)
    {
        var state = Initial(request);
        var prefix = new List<Stmt>();
        if (!LiftRange(request, insns, header.First, header.Last - 1, state, prefix, null, diagnostics))
            return null;
        if (!TryBranchCondition(insns[header.Last], state, out var condition, out var why))
        {
            diagnostics.Add(why);
            return null;
        }

        var takenState = state.Clone();
        var takenStatements = new List<Stmt>();
        if (!LiftRange(request, insns, taken.First, taken.Last - 1, takenState, takenStatements, null, diagnostics))
            return null;
        if (!AppendReturn(request, takenState, takenStatements, insns[taken.Last].Address, diagnostics))
            return null;

        var otherState = state.Clone();
        var otherStatements = new List<Stmt>();
        if (!LiftRange(request, insns, fallthrough.First, fallthrough.Last - 1, otherState, otherStatements, null, diagnostics))
            return null;
        if (!AppendReturn(request, otherState, otherStatements, insns[fallthrough.Last].Address, diagnostics))
            return null;

        prefix.Add(new IfStmt(condition, takenStatements, otherStatements)
        {
            Addresses = [insns[header.Last].Address]
        });
        return prefix;
    }

    private static List<Stmt>? LiftWhile(RecoveryRequest request, List<Arm64Instruction> insns, Block? prelude, Block header, Block body, Block exit, List<string> diagnostics)
    {
        for (var i = header.First; i < header.Last; i++)
        {
            if (insns[i].Mnemonic is not (Arm64Mnemonic.CMP or Arm64Mnemonic.NOP))
            {
                diagnostics.Add($"0x{insns[i].Address:X}: the loop header contains {insns[i].Mnemonic}, which is outside the single-while subset.");
                return null;
            }
        }

        var state = Initial(request);
        var statements = new List<Stmt>();
        if (prelude is not null && !LiftRange(request, insns, prelude.First, prelude.Last, state, statements, null, diagnostics))
            return null;

        var carried = new List<int>();
        for (var i = body.First; i < body.Last; i++)
        {
            if (TryDest(insns[i], out var reg, out _))
                if (!carried.Contains(reg))
                    carried.Add(reg);
        }

        var names = new Dictionary<int, string>();
        var declare = new List<Stmt>();
        foreach (var reg in carried)
        {
            if (!state.TryGet(reg, out var slot))
            {
                diagnostics.Add($"Loop register w/x{reg} is written in the body but has no value before the loop.");
                return null;
            }
            var name = "local_" + (slot.Width == 32 ? "w" : "x") + reg;
            names[reg] = name;
            declare.Add(new DeclareStmt(name, slot.Value.Type, slot.Value) { Addresses = [insns[header.First].Address] });
        }

        var loopState = state.Clone();
        foreach (var reg in carried)
        {
            var slot = state.Get(reg);
            loopState.Set(reg, new LocalExpr(names[reg], slot.Value.Type), slot.Width);
        }

        if (!LiftRange(request, insns, header.First, header.Last - 1, loopState, statements: [], names, diagnostics))
            return null;
        if (!TryBranchCondition(insns[header.Last], loopState, out var taken, out var why))
        {
            diagnostics.Add(why);
            return null;
        }

        var exitIsTaken = BranchTargetAddress(insns[header.Last]) == insns[exit.First].Address;
        var stay = exitIsTaken ? Invert(taken) : taken;
        if (stay is null)
        {
            diagnostics.Add("The loop condition could not be inverted.");
            return null;
        }

        var bodyStatements = new List<Stmt>();
        var bodyState = loopState.Clone();
        if (!LiftRange(request, insns, body.First, body.Last - 1, bodyState, bodyStatements, names, diagnostics))
            return null;

        statements.AddRange(declare);
        statements.Add(new WhileStmt(stay, bodyStatements) { Addresses = [insns[header.Last].Address] });

        var exitState = loopState.Clone();
        if (!LiftRange(request, insns, exit.First, exit.Last - 1, exitState, statements, names, diagnostics))
            return null;
        if (!AppendReturn(request, exitState, statements, insns[exit.Last].Address, diagnostics))
            return null;
        return statements;
    }

    private static bool AppendReturn(RecoveryRequest request, RegState state, List<Stmt> statements, ulong address, List<string> diagnostics)
    {
        if (request.Contract.ReturnType == PrimitiveKind.Void)
        {
            statements.Add(new ReturnStmt(null) { Addresses = [address] });
            return true;
        }

        if (request.Contract.ReturnType is PrimitiveKind.Single or PrimitiveKind.Double)
        {
            if (!state.TryGetFloat(0, out var floatSlot))
            {
                diagnostics.Add("The floating-point return register does not hold a lifted value.");
                return false;
            }
            statements.Add(new ReturnStmt(floatSlot.Value) { Addresses = [address] });
            return true;
        }

        var width = request.Contract.ReturnType is PrimitiveKind.Int64 or PrimitiveKind.UInt64 ? 64 : 32;
        if (!state.TryGet(0, out var slot) || slot.Width < width && slot.Value is not ParamExpr)
        {
            diagnostics.Add("The return register does not hold a lifted value.");
            return false;
        }

        Expr value = slot.Value;
        if (request.Contract.ReturnType == PrimitiveKind.Boolean && value.Type != PrimitiveKind.Boolean)
            value = new BinaryExpr("!=", value, new ConstExpr(0, value.Type), PrimitiveKind.Boolean);
        statements.Add(new ReturnStmt(value) { Addresses = [address] });
        return true;
    }

    private static bool LiftRange(RecoveryRequest request, List<Arm64Instruction> insns, int first, int lastInclusive, RegState state, List<Stmt> statements, Dictionary<int, string>? locals, List<string> diagnostics)
    {
        if (lastInclusive < first)
            return true;
        for (var i = first; i <= lastInclusive; i++)
        {
            if (!LiftOne(request, insns[i], state, statements, locals, out var error))
            {
                diagnostics.Add(error);
                return false;
            }
        }
        return true;
    }

    private static bool LiftOne(RecoveryRequest request, Arm64Instruction insn, RegState state, List<Stmt> statements, Dictionary<int, string>? locals, out string error)
    {
        error = "";
        if (insn.FinalOpShiftType != Arm64ShiftType.NONE || insn.FinalOpExtendType != Arm64ExtendType.NONE)
        {
            error = $"0x{insn.Address:X}: shifted or extended operands are not lifted ({insn}).";
            return false;
        }

        switch (insn.Mnemonic)
        {
            case Arm64Mnemonic.NOP:
                return true;
            case Arm64Mnemonic.MOV:
            case Arm64Mnemonic.MOVZ:
                return Move(insn, state, statements, locals, invert: false, out error);
            case Arm64Mnemonic.MOVN:
                return Move(insn, state, statements, locals, invert: true, out error);
            case Arm64Mnemonic.ADD:
            case Arm64Mnemonic.SUB:
            case Arm64Mnemonic.MUL:
            case Arm64Mnemonic.AND:
            case Arm64Mnemonic.ORR:
            case Arm64Mnemonic.EOR:
                return Arithmetic(insn, state, statements, locals, out error);
            case Arm64Mnemonic.CMP:
                return Compare(insn, state, out error);
            case Arm64Mnemonic.CSEL:
                return Select(insn, state, statements, locals, out error);
            case Arm64Mnemonic.FADD:
                return FloatAdd(insn, state, statements, locals, out error);
            case Arm64Mnemonic.ADRP:
                return Adrp(insn, state, statements, locals, out error);
            case Arm64Mnemonic.LDR:
                return Load(request, insn, state, statements, locals, out error);
            case Arm64Mnemonic.STR:
                return Store(request, insn, state, out error, statements);
            case Arm64Mnemonic.BL:
                return Call(request, insn, state, statements, out error);
            default:
                error = $"0x{insn.Address:X}: {insn.Mnemonic} is not in the implemented subset.";
                return false;
        }
    }

    private static bool Move(Arm64Instruction insn, RegState state, List<Stmt> statements, Dictionary<int, string>? locals, bool invert, out string error)
    {
        error = "";
        if (!TryDest(insn, out var dest, out var width))
        {
            error = $"0x{insn.Address:X}: move destination is not a general register.";
            return false;
        }

        Expr value;
        if (insn.Op1Kind == Arm64OperandKind.Immediate)
        {
            var raw = insn.Op1Imm;
            if (invert)
            {
                raw = width == 32 ? unchecked((int)~(uint)raw) : ~raw;
            }
            value = new ConstExpr(raw, width == 32 ? PrimitiveKind.Int32 : PrimitiveKind.Int64);
        }
        else if (insn.Op1Kind == Arm64OperandKind.Register)
        {
            if (!TryReadReg(insn.Op1Reg, state, out value, out error))
                return false;
            if (invert)
            {
                error = $"0x{insn.Address:X}: MOVN of a register is not lifted.";
                return false;
            }
        }
        else
        {
            error = $"0x{insn.Address:X}: move source is not an immediate or register.";
            return false;
        }

        Write(dest, width, value, state, statements, locals, insn.Address);
        return true;
    }

    private static bool Arithmetic(Arm64Instruction insn, RegState state, List<Stmt> statements, Dictionary<int, string>? locals, out string error)
    {
        error = "";
        if (insn.Op3Kind == Arm64OperandKind.Immediate && insn.Op3Imm != 0)
        {
            error = $"0x{insn.Address:X}: a non-zero shift immediate is not lifted.";
            return false;
        }
        if (!TryDest(insn, out var dest, out var width))
        {
            error = $"0x{insn.Address:X}: arithmetic destination is not a general register.";
            return false;
        }
        if (!TryOperand(insn, 1, state, out var left, out error) || !TryOperand(insn, 2, state, out var right, out error))
            return false;
        if (insn.Mnemonic == Arm64Mnemonic.ADD && left is AddressExpr address && right is ConstExpr constant)
        {
            Write(dest, 64, new AddressExpr(address.Address + (ulong)constant.Value), state, statements, locals, insn.Address);
            return true;
        }
        var op = insn.Mnemonic switch
        {
            Arm64Mnemonic.ADD => "+",
            Arm64Mnemonic.SUB => "-",
            Arm64Mnemonic.MUL => "*",
            Arm64Mnemonic.AND => "&",
            Arm64Mnemonic.ORR => "|",
            Arm64Mnemonic.EOR => "^",
            _ => ""
        };
        var type = width == 32 ? PrimitiveKind.Int32 : PrimitiveKind.Int64;
        if (left.Type is PrimitiveKind.UInt32 or PrimitiveKind.UInt64)
            type = left.Type;
        state.FlagsValid = false;
        Write(dest, width, new BinaryExpr(op, left, right, type), state, statements, locals, insn.Address);
        return true;
    }

    private static bool Compare(Arm64Instruction insn, RegState state, out string error)
    {
        error = "";
        if (!TryOperand(insn, 0, state, out var left, out error) || !TryOperand(insn, 1, state, out var right, out error))
            return false;
        state.FlagLeft = left;
        state.FlagRight = right;
        state.FlagWidth = insn.Op0Reg.ToString().StartsWith('X') ? 64 : 32;
        state.FlagsValid = true;
        return true;
    }

    private static bool Select(Arm64Instruction insn, RegState state, List<Stmt> statements, Dictionary<int, string>? locals, out string error)
    {
        error = "";
        if (!TryDest(insn, out var dest, out var width))
        {
            error = $"0x{insn.Address:X}: CSEL destination is not a general register.";
            return false;
        }
        if (!TryFlagCondition(insn.FinalOpConditionCode, state, out var condition, out error))
            return false;
        if (!TryOperand(insn, 1, state, out var whenTrue, out error) || !TryOperand(insn, 2, state, out var whenFalse, out error))
            return false;
        Write(dest, width, new ConditionalExpr(condition, whenTrue, whenFalse), state, statements, locals, insn.Address);
        return true;
    }

    private static bool FloatAdd(Arm64Instruction insn, RegState state, List<Stmt> statements, Dictionary<int, string>? locals, out string error)
    {
        error = "";
        if (!TryParseFloat(insn.Op0Reg, out var dest, out var width)
            || !TryParseFloat(insn.Op1Reg, out var leftIndex, out _)
            || !TryParseFloat(insn.Op2Reg, out var rightIndex, out _))
        {
            error = $"0x{insn.Address:X}: FADD operands are {insn.Op0Reg}, {insn.Op1Reg}, {insn.Op2Reg}. Only scalar S and D registers are lifted.";
            return false;
        }
        if (!state.TryGetFloat(leftIndex, out var left) || !state.TryGetFloat(rightIndex, out var right))
        {
            error = $"0x{insn.Address:X}: FADD reads a floating-point register that has no lifted value.";
            return false;
        }
        var type = width == 64 ? PrimitiveKind.Double : PrimitiveKind.Single;
        state.SetFloat(dest, new BinaryExpr("+", left.Value, right.Value, type), width);
        return true;
    }

    private static bool TryFlagCondition(Arm64ConditionCode code, RegState state, out Expr condition, out string error)
    {
        condition = new ConstExpr(0, PrimitiveKind.Boolean);
        if (!state.FlagsValid || state.FlagLeft is null || state.FlagRight is null)
        {
            error = "The conditional select does not follow a lifted CMP.";
            return false;
        }
        var signed = code is Arm64ConditionCode.EQ or Arm64ConditionCode.NE or Arm64ConditionCode.LT or Arm64ConditionCode.GT or Arm64ConditionCode.LE or Arm64ConditionCode.GE;
        var opText = code switch
        {
            Arm64ConditionCode.EQ => "==",
            Arm64ConditionCode.NE => "!=",
            Arm64ConditionCode.LT or Arm64ConditionCode.CC => "<",
            Arm64ConditionCode.GT or Arm64ConditionCode.HI => ">",
            Arm64ConditionCode.LE or Arm64ConditionCode.LS => "<=",
            Arm64ConditionCode.GE or Arm64ConditionCode.CS => ">=",
            _ => ""
        };
        if (opText.Length == 0)
        {
            error = "Condition " + code + " is not lifted.";
            return false;
        }
        if (!signed)
            opText = "u" + opText;
        condition = new BinaryExpr(opText, state.FlagLeft, state.FlagRight, PrimitiveKind.Boolean);
        error = "";
        return true;
    }

    private static bool Adrp(Arm64Instruction insn, RegState state, List<Stmt> statements, Dictionary<int, string>? locals, out string error)
    {
        error = "";
        if (!TryDest(insn, out var dest, out _))
        {
            error = $"0x{insn.Address:X}: ADRP destination is not a general register.";
            return false;
        }
        var page = (insn.Address & ~0xFFFul) + (ulong)insn.Op1Imm;
        Write(dest, 64, new AddressExpr(page), state, statements, locals, insn.Address);
        return true;
    }

    private static bool Load(RecoveryRequest request, Arm64Instruction insn, RegState state, List<Stmt> statements, Dictionary<int, string>? locals, out string error)
    {
        error = "";
        if (insn.MemIndexMode != Arm64MemoryIndexMode.Offset || insn.MemAddendReg != Arm64Register.INVALID)
        {
            error = $"0x{insn.Address:X}: only unsigned immediate loads are lifted.";
            return false;
        }
        if (!TryDest(insn, out var dest, out var width))
        {
            error = $"0x{insn.Address:X}: load destination is not a general register.";
            return false;
        }
        if (!TryReadReg(insn.MemBase, state, out var baseExpr, out error))
            return false;
        var field = MatchField(request, baseExpr, insn.MemOffset);
        if (field is null)
        {
            error = $"0x{insn.Address:X}: load is not a known field access. No pointer was invented.";
            return false;
        }
        if (WidthOf(field.Type) != width)
        {
            error = $"0x{insn.Address:X}: field {field.Name} width does not match the load.";
            return false;
        }
        var instance = field.IsStatic ? null : baseExpr;
        Write(dest, width, new FieldExpr(instance, field.Name, field.Type), state, statements, locals, insn.Address);
        return true;
    }

    private static bool Store(RecoveryRequest request, Arm64Instruction insn, RegState state, out string error, List<Stmt> statements)
    {
        error = "";
        if (insn.MemIndexMode != Arm64MemoryIndexMode.Offset || insn.MemAddendReg != Arm64Register.INVALID)
        {
            error = $"0x{insn.Address:X}: only unsigned immediate stores are lifted.";
            return false;
        }
        if (!TryReadReg(insn.MemBase, state, out var baseExpr, out error))
            return false;
        if (!TryReadReg(insn.Op0Reg, state, out var value, out error))
            return false;
        var field = MatchField(request, baseExpr, insn.MemOffset);
        if (field is null)
        {
            error = $"0x{insn.Address:X}: store is not a known field access. No pointer was invented.";
            return false;
        }
        statements.Add(new StoreFieldStmt(field.IsStatic ? null : baseExpr, field.Name, value)
        {
            Addresses = [insn.Address]
        });
        return true;
    }

    private static bool Call(RecoveryRequest request, Arm64Instruction insn, RegState state, List<Stmt> statements, out string error)
    {
        error = "";
        var target = insn.BranchTarget;
        if (request.CallTargets is null || !request.CallTargets.TryGetValue(target, out var callee))
        {
            error = $"0x{insn.Address:X}: call target 0x{target:X} is not in the method map.";
            return false;
        }
        if (callee.Parameters.Count > 8)
        {
            error = $"0x{insn.Address:X}: calls with stack arguments are not lifted.";
            return false;
        }

        var args = new List<Expr>();
        var reg = callee.IsStatic ? 0 : 1;
        if (!callee.IsStatic)
        {
            if (!state.TryGet(0, out var receiver) || receiver.Value is not (ThisExpr or ParamExpr or FieldExpr or CallExpr))
            {
                error = $"0x{insn.Address:X}: the call receiver is not a lifted reference.";
                return false;
            }
        }
        foreach (var parameter in callee.Parameters)
        {
            if (!state.TryGet(reg, out var slot))
            {
                error = $"0x{insn.Address:X}: argument {parameter.Name} is not in a lifted register.";
                return false;
            }
            args.Add(slot.Value);
            reg++;
        }

        var call = new CallExpr(callee.Name, args, callee.ReturnType);
        state.Clear();
        state.FlagsValid = false;
        if (callee.ReturnType == PrimitiveKind.Void)
        {
            statements.Add(new ExpressionStmt(call) { Addresses = [insn.Address] });
            return true;
        }

        var temp = "call_" + insn.Address.ToString("X");
        var width = callee.ReturnType is PrimitiveKind.Int64 or PrimitiveKind.UInt64 or PrimitiveKind.Reference ? 64 : 32;
        statements.Add(new DeclareStmt(temp, callee.ReturnType == PrimitiveKind.Boolean ? PrimitiveKind.Int32 : callee.ReturnType, call)
        {
            Addresses = [insn.Address]
        });
        state.Set(0, new LocalExpr(temp, callee.ReturnType), width);
        return true;
    }

    private static void Write(int reg, int width, Expr value, RegState state, List<Stmt> statements, Dictionary<int, string>? locals, ulong address)
    {
        if (locals is not null && locals.TryGetValue(reg, out var name))
        {
            statements.Add(new AssignStmt(name, value) { Addresses = [address] });
            state.Set(reg, new LocalExpr(name, value.Type), width);
            return;
        }
        state.Set(reg, value, width);
    }

    private static FieldContract? MatchField(RecoveryRequest request, Expr baseExpr, long offset)
    {
        if (baseExpr is ThisExpr)
            return request.Contract.Fields.FirstOrDefault(f => !f.IsStatic && f.InstanceOffset == offset);
        if (baseExpr is AddressExpr address)
        {
            var absolute = address.Address + (ulong)offset;
            return request.Contract.Fields.FirstOrDefault(f => f.IsStatic && f.AbsoluteAddress == absolute);
        }
        return null;
    }

    private static bool TryBranchCondition(Arm64Instruction insn, RegState state, out Expr condition, out string error)
    {
        condition = new ConstExpr(0, PrimitiveKind.Boolean);
        error = "";
        if (insn.Mnemonic is Arm64Mnemonic.CBZ or Arm64Mnemonic.CBNZ)
        {
            if (!TryReadReg(insn.Op0Reg, state, out var value, out error))
                return false;
            var op = insn.Mnemonic == Arm64Mnemonic.CBZ ? "==" : "!=";
            condition = new BinaryExpr(op, value, new ConstExpr(0, value.Type), PrimitiveKind.Boolean);
            return true;
        }

        if (insn.Mnemonic is Arm64Mnemonic.TBZ or Arm64Mnemonic.TBNZ)
            return TryBitTest(insn, state, out condition, out error);

        if (insn.Mnemonic != Arm64Mnemonic.B || insn.MnemonicConditionCode == Arm64ConditionCode.NONE)
        {
            error = $"0x{insn.Address:X}: expected a conditional branch.";
            return false;
        }
        if (!state.FlagsValid || state.FlagLeft is null || state.FlagRight is null)
        {
            error = $"0x{insn.Address:X}: the branch does not follow a lifted CMP.";
            return false;
        }

        var signed = insn.MnemonicConditionCode is Arm64ConditionCode.EQ or Arm64ConditionCode.NE or Arm64ConditionCode.LT or Arm64ConditionCode.GT or Arm64ConditionCode.LE or Arm64ConditionCode.GE;
        var opText = insn.MnemonicConditionCode switch
        {
            Arm64ConditionCode.EQ => "==",
            Arm64ConditionCode.NE => "!=",
            Arm64ConditionCode.LT or Arm64ConditionCode.CC => "<",
            Arm64ConditionCode.GT or Arm64ConditionCode.HI => ">",
            Arm64ConditionCode.LE or Arm64ConditionCode.LS => "<=",
            Arm64ConditionCode.GE or Arm64ConditionCode.CS => ">=",
            _ => ""
        };
        if (opText.Length == 0)
        {
            error = $"0x{insn.Address:X}: condition {insn.MnemonicConditionCode} is not lifted.";
            return false;
        }
        if (!signed)
            opText = "u" + opText;
        condition = new BinaryExpr(opText, state.FlagLeft, state.FlagRight, PrimitiveKind.Boolean);
        return true;
    }

    private static Expr? Invert(Expr condition)
    {
        if (condition is not BinaryExpr binary)
            return null;
        var op = binary.Op switch
        {
            "==" => "!=",
            "!=" => "==",
            "<" => ">=",
            ">" => "<=",
            "<=" => ">",
            ">=" => "<",
            "u<" => "u>=",
            "u>" => "u<=",
            "u<=" => "u>",
            "u>=" => "u<",
            _ => ""
        };
        return op.Length == 0 ? null : new BinaryExpr(op, binary.Left, binary.Right, PrimitiveKind.Boolean);
    }

    private static bool TryOperand(Arm64Instruction insn, int index, RegState state, out Expr value, out string error)
    {
        var kind = index switch
        {
            0 => insn.Op0Kind,
            1 => insn.Op1Kind,
            2 => insn.Op2Kind,
            _ => Arm64OperandKind.None
        };
        var reg = index switch
        {
            0 => insn.Op0Reg,
            1 => insn.Op1Reg,
            2 => insn.Op2Reg,
            _ => Arm64Register.INVALID
        };
        var imm = index switch
        {
            0 => insn.Op0Imm,
            1 => insn.Op1Imm,
            2 => insn.Op2Imm,
            _ => 0
        };
        if (kind == Arm64OperandKind.Immediate)
        {
            value = new ConstExpr(imm, PrimitiveKind.Int32);
            error = "";
            return true;
        }
        if (kind == Arm64OperandKind.Register)
            return TryReadReg(reg, state, out value, out error);
        value = new ConstExpr(0, PrimitiveKind.Int32);
        error = $"0x{insn.Address:X}: operand {index} is {kind}, which is not lifted.";
        return false;
    }

    private static bool TryReadReg(Arm64Register reg, RegState state, out Expr value, out string error)
    {
        value = new ConstExpr(0, PrimitiveKind.Int32);
        error = "";
        var name = reg.ToString();
        if (name is "WZR" or "XZR" or "W31" or "X31")
        {
            value = new ConstExpr(0, name.StartsWith('X') ? PrimitiveKind.Int64 : PrimitiveKind.Int32);
            return true;
        }
        if (!TryParseGpr(reg, out var index, out var width))
        {
            error = $"Register {reg} is not a general-purpose register in the implemented subset.";
            return false;
        }
        if (!state.TryGet(index, out var slot))
        {
            error = $"Register {reg} has no lifted value.";
            return false;
        }
        if (width == 32 && slot.Width == 64 && slot.Value is not (ParamExpr or ConstExpr or LocalExpr))
        {
            error = $"Reading {reg} would narrow a lifted 64-bit value that is not a parameter or constant.";
            return false;
        }
        value = slot.Value;
        return true;
    }

    private static bool TryDest(Arm64Instruction insn, out int reg, out int width) =>
        TryParseGpr(insn.Op0Reg, out reg, out width);

    private static bool TryParseFloat(Arm64Register reg, out int index, out int width)
    {
        index = 0;
        width = 0;
        var name = reg.ToString();
        if (name.Length < 2 || name[0] is not ('S' or 'D'))
            return false;
        if (!int.TryParse(name[1..], out index) || index is < 0 or > 31)
            return false;
        width = name[0] == 'D' ? 64 : 32;
        return true;
    }

    private static bool TryParseGpr(Arm64Register reg, out int index, out int width)
    {
        index = 0;
        width = 0;
        var name = reg.ToString();
        if (name.Length < 2 || name[0] is not ('W' or 'X'))
            return false;
        if (!int.TryParse(name[1..], out index) || index is < 0 or > 30)
            return false;
        width = name[0] == 'W' ? 32 : 64;
        return true;
    }

    private static RegState Initial(RecoveryRequest request)
    {
        var state = new RegState();
        var start = 0;
        if (!request.Contract.IsStatic)
        {
            state.Set(0, new ThisExpr(), 64);
            start = 1;
        }
        var floatReg = 0;
        foreach (var parameter in request.Contract.Parameters)
        {
            if (parameter.Type is PrimitiveKind.Single or PrimitiveKind.Double)
            {
                var floatWidth = parameter.Type == PrimitiveKind.Double ? 64 : 32;
                if (floatReg < 8)
                    state.SetFloat(floatReg, new ParamExpr(parameter.Name, parameter.Type), floatWidth);
                floatReg++;
                continue;
            }
            var width = parameter.Type is PrimitiveKind.Int64 or PrimitiveKind.UInt64 or PrimitiveKind.Reference ? 64 : 32;
            if (start <= 30)
                state.Set(start, new ParamExpr(parameter.Name, parameter.Type), width);
            start++;
        }
        return state;
    }

    private static int WidthOf(PrimitiveKind type) => type is PrimitiveKind.Int64 or PrimitiveKind.UInt64 or PrimitiveKind.Reference ? 64 : 32;

    private static bool TryMatchIfReturn(List<Arm64Instruction> insns, List<Block> reachable, Block entry, out Block? taken, out Block? fallthrough)
    {
        taken = null;
        fallthrough = null;
        if (reachable.Count != 3 || entry.Succ.Count != 2)
            return false;
        if (insns[entry.Last].Mnemonic is not (Arm64Mnemonic.B or Arm64Mnemonic.CBZ or Arm64Mnemonic.CBNZ or Arm64Mnemonic.TBZ or Arm64Mnemonic.TBNZ))
            return false;
        var target = BranchTargetAddress(insns[entry.Last]);
        var takenBlock = entry.Succ.Select(i => reachable.First(b => b.Id == i)).FirstOrDefault(b => insns[b.First].Address == target);
        var fallthroughBlock = entry.Succ.Select(i => reachable.First(b => b.Id == i)).FirstOrDefault(b => !ReferenceEquals(b, takenBlock));
        taken = takenBlock;
        fallthrough = fallthroughBlock;
        if (taken is null || fallthrough is null)
            return false;
        return EndsWith(insns, taken, Arm64Mnemonic.RET) && taken.Succ.Count == 0
            && EndsWith(insns, fallthrough, Arm64Mnemonic.RET) && fallthrough.Succ.Count == 0;
    }

    private static bool TryMatchWhile(List<Arm64Instruction> insns, List<Block> blocks, List<Block> reachable, Block entry, out Block? prelude, out Block? header, out Block? body, out Block? exit)
    {
        prelude = null;
        header = null;
        body = null;
        exit = null;
        foreach (var candidate in reachable)
        {
            if (candidate.Succ.Count != 2)
                continue;
            var succs = candidate.Succ.Select(id => blocks[id]).ToList();
            var loopBody = succs.FirstOrDefault(s => s.Succ.Count == 1 && s.Succ[0] == candidate.Id && EndsWith(insns, s, Arm64Mnemonic.B));
            var loopExit = succs.FirstOrDefault(s => !ReferenceEquals(s, loopBody));
            if (loopBody is null || loopExit is null || !EndsWith(insns, loopExit, Arm64Mnemonic.RET))
                continue;
            header = candidate;
            body = loopBody;
            exit = loopExit;
            break;
        }
        if (header is null || body is null || exit is null)
            return false;

        if (entry.Id == header.Id)
        {
            prelude = null;
        }
        else if (entry.Succ.Count == 1 && entry.Succ[0] == header.Id && !EndsWith(insns, entry, Arm64Mnemonic.B) && !EndsWith(insns, entry, Arm64Mnemonic.RET))
        {
            prelude = entry;
        }
        else
        {
            return false;
        }

        var expected = new HashSet<int> { header.Id, body.Id, exit.Id };
        if (prelude is not null)
            expected.Add(prelude.Id);
        return reachable.All(b => expected.Contains(b.Id));
    }

    private static bool EndsWith(List<Arm64Instruction> insns, Block block, Arm64Mnemonic mnemonic) =>
        insns[block.Last].Mnemonic == mnemonic;

    private static List<Block> BuildCfg(List<Arm64Instruction> insns)
    {
        var indexByAddress = new Dictionary<ulong, int>();
        for (var i = 0; i < insns.Count; i++)
            indexByAddress[insns[i].Address] = i;

        var leaders = new SortedSet<int> { 0 };
        for (var i = 0; i < insns.Count; i++)
        {
            if (!IsTerminator(insns[i]))
                continue;
            if (FallsThrough(insns[i]) && i + 1 < insns.Count)
                leaders.Add(i + 1);
            var target = BranchTargetAddress(insns[i]);
            if (target is not null && indexByAddress.ContainsKey(target.Value))
                leaders.Add(indexByAddress[target.Value]);
            else if (IsTerminator(insns[i]) && insns[i].Mnemonic != Arm64Mnemonic.RET && i + 1 < insns.Count && !FallsThrough(insns[i]))
                leaders.Add(i + 1);
        }

        var leaderList = leaders.ToList();
        var blocks = new List<Block>();
        for (var b = 0; b < leaderList.Count; b++)
        {
            var first = leaderList[b];
            var last = (b + 1 < leaderList.Count ? leaderList[b + 1] : insns.Count) - 1;
            blocks.Add(new Block { Id = b, First = first, Last = last });
        }

        var blockByInstruction = new int[insns.Count];
        foreach (var block in blocks)
        {
            for (var i = block.First; i <= block.Last; i++)
                blockByInstruction[i] = block.Id;
        }

        foreach (var block in blocks)
        {
            var term = insns[block.Last];
            if (term.Mnemonic == Arm64Mnemonic.BR)
            {
                block.Indirect = true;
                continue;
            }
            if (term.Mnemonic == Arm64Mnemonic.RET)
                continue;

            if (IsTerminator(term))
            {
                var target = BranchTargetAddress(term);
                if (target is null)
                {
                    block.External = true;
                }
                else if (!indexByAddress.TryGetValue(target.Value, out var targetIndex))
                {
                    block.External = true;
                }
                else
                {
                    block.Succ.Add(blockByInstruction[targetIndex]);
                }
                if (FallsThrough(term))
                {
                    if (block.Last + 1 >= insns.Count)
                        block.External = true;
                    else
                        block.Succ.Add(blockByInstruction[block.Last + 1]);
                }
            }
            else if (block.Last + 1 < insns.Count)
            {
                block.Succ.Add(blockByInstruction[block.Last + 1]);
            }
        }
        return blocks;
    }

    private static bool IsTerminator(Arm64Instruction insn) =>
        insn.Mnemonic is Arm64Mnemonic.B or Arm64Mnemonic.CBZ or Arm64Mnemonic.CBNZ or Arm64Mnemonic.RET or Arm64Mnemonic.BR
        || insn.Mnemonic is Arm64Mnemonic.TBZ or Arm64Mnemonic.TBNZ;

    private static bool FallsThrough(Arm64Instruction insn) =>
        insn.Mnemonic is Arm64Mnemonic.CBZ or Arm64Mnemonic.CBNZ or Arm64Mnemonic.TBZ or Arm64Mnemonic.TBNZ
        || (insn.Mnemonic == Arm64Mnemonic.B && insn.MnemonicConditionCode != Arm64ConditionCode.NONE);

    private static ulong? BranchTargetAddress(Arm64Instruction insn)
    {
        if (insn.Mnemonic is Arm64Mnemonic.B or Arm64Mnemonic.BL)
            return insn.BranchTarget;
        if (insn.Mnemonic is Arm64Mnemonic.CBZ or Arm64Mnemonic.CBNZ)
            return PcRelative(insn, 1);
        if (insn.Mnemonic is Arm64Mnemonic.TBZ or Arm64Mnemonic.TBNZ)
            return unchecked(insn.Address + (ulong)insn.Op2Imm);
        return null;
    }

    private static bool TryBitTest(Arm64Instruction insn, RegState state, out Expr condition, out string error)
    {
        condition = new ConstExpr(0, PrimitiveKind.Boolean);
        if (!TryReadReg(insn.Op0Reg, state, out var value, out error))
            return false;
        if (value.Type is PrimitiveKind.Reference)
        {
            error = $"0x{insn.Address:X}: {insn.Mnemonic} tests a reference. That test was not rewritten as an integer.";
            return false;
        }

        var bit = insn.Op1Imm;
        if (bit is < 0 or > 62)
        {
            error = $"0x{insn.Address:X}: bit {bit} of {insn.Mnemonic} is outside the lifted range.";
            return false;
        }
        if (bit > 31 && value.Type is not (PrimitiveKind.Int64 or PrimitiveKind.UInt64))
        {
            error = $"0x{insn.Address:X}: bit {bit} does not fit the lifted value.";
            return false;
        }

        var maskType = value.Type is PrimitiveKind.Int64 or PrimitiveKind.UInt64 ? value.Type : PrimitiveKind.Int32;
        var masked = new BinaryExpr("&", value, new ConstExpr(1L << (int)bit, maskType), maskType);
        var op = insn.Mnemonic == Arm64Mnemonic.TBZ ? "==" : "!=";
        condition = new BinaryExpr(op, masked, new ConstExpr(0, maskType), PrimitiveKind.Boolean);
        error = "";
        return true;
    }

    private static ulong PcRelative(Arm64Instruction insn, int operand) =>
        operand == 1 ? insn.Op1PcRelImm : insn.Op0PcRelImm;

    private static List<Block> Reachable(List<Block> blocks)
    {
        var seen = new HashSet<int>();
        var stack = new Stack<int>();
        stack.Push(0);
        while (stack.Count > 0)
        {
            var id = stack.Pop();
            if (!seen.Add(id))
                continue;
            foreach (var succ in blocks[id].Succ)
                stack.Push(succ);
        }
        return blocks.Where(b => seen.Contains(b.Id)).ToList();
    }

    private static List<NativeInstruction> Render(byte[] code, List<Arm64Instruction> instructions)
    {
        var list = new List<NativeInstruction>(instructions.Count);
        for (var i = 0; i < instructions.Count; i++)
        {
            var slice = code.AsSpan(i * 4, 4);
            list.Add(new NativeInstruction
            {
                Address = "0x" + instructions[i].Address.ToString("X"),
                Bytes = Convert.ToHexString(slice).ToLowerInvariant(),
                Text = instructions[i].ToString()
            });
        }
        return list;
    }

    private static List<ProvenanceLink> Provenance(IReadOnlyList<Stmt> statements)
    {
        var links = new List<ProvenanceLink>();
        void Walk(Stmt statement)
        {
            links.Add(new ProvenanceLink
            {
                Operation = statement switch
                {
                    ReturnStmt ret => ret.Value is null ? "return" : "return " + ret.Value.Describe(),
                    AssignStmt assign => assign.Local + " = " + assign.Value.Describe(),
                    DeclareStmt declare => declare.Local + " = " + declare.Value.Describe(),
                    StoreFieldStmt store => store.Field + " = " + store.Value.Describe(),
                    IfStmt ifs => "if " + ifs.Condition.Describe(),
                    WhileStmt loop => "while " + loop.Condition.Describe(),
                    ExpressionStmt expr => expr.Value.Describe(),
                    _ => statement.GetType().Name
                },
                NativeAddresses = statement.Addresses.Select(a => "0x" + a.ToString("X")).ToList()
            });
            if (statement is IfStmt nested)
            {
                foreach (var child in nested.WhenTrue.Concat(nested.WhenFalse))
                    Walk(child);
            }
            else if (statement is WhileStmt loop)
            {
                foreach (var child in loop.Body)
                    Walk(child);
            }
        }
        foreach (var statement in statements)
            Walk(statement);
        return links;
    }

    private static MethodBodyResult Fail(RecoveryRequest request, string diagnostic, IReadOnlyList<NativeInstruction> instructions) =>
        new()
        {
            Status = MethodRecoveryStatus.Failed,
            Diagnostics = [diagnostic],
            Instructions = instructions,
            CSharp = CSharpEmitter.Placeholder(MethodRecoveryStatus.Failed, [diagnostic]),
            CSharpIsPlaceholder = true
        };

    private static MethodBodyResult Incomplete(RecoveryRequest request, MethodRecoveryStatus status, IReadOnlyList<NativeInstruction> instructions, IReadOnlyList<string> diagnostics, string? MethodKindNote = null)
    {
        var notes = diagnostics.ToList();
        if (MethodKindNote is not null)
            notes.Insert(0, "Kind: " + MethodKindNote);
        return new MethodBodyResult
        {
            Status = status,
            Diagnostics = notes,
            Instructions = instructions,
            CSharp = CSharpEmitter.Placeholder(status, notes),
            CSharpIsPlaceholder = true
        };
    }

    private sealed class Block
    {
        public int Id { get; init; }
        public int First { get; init; }
        public int Last { get; init; }
        public List<int> Succ { get; } = [];
        public bool External { get; set; }
        public bool Indirect { get; set; }
    }

    private sealed class RegSlot
    {
        public required Expr Value { get; init; }
        public int Width { get; init; }
    }

    private sealed class RegState
    {
        private readonly RegSlot?[] _regs = new RegSlot?[31];
        private readonly RegSlot?[] _floats = new RegSlot?[32];
        public Expr? FlagLeft { get; set; }
        public Expr? FlagRight { get; set; }
        public int FlagWidth { get; set; }
        public bool FlagsValid { get; set; }

        public void Set(int index, Expr value, int width) => _regs[index] = new RegSlot { Value = value, Width = width };

        public bool TryGet(int index, out RegSlot slot)
        {
            slot = _regs[index]!;
            return _regs[index] is not null;
        }

        public RegSlot Get(int index) => _regs[index] ?? throw new InvalidOperationException("Register is empty.");

        public void SetFloat(int index, Expr value, int width) => _floats[index] = new RegSlot { Value = value, Width = width };

        public bool TryGetFloat(int index, out RegSlot slot)
        {
            slot = _floats[index]!;
            return _floats[index] is not null;
        }

        public void Clear()
        {
            Array.Clear(_regs);
            Array.Clear(_floats);
        }

        public RegState Clone()
        {
            var copy = new RegState
            {
                FlagLeft = FlagLeft,
                FlagRight = FlagRight,
                FlagWidth = FlagWidth,
                FlagsValid = FlagsValid
            };
            Array.Copy(_regs, copy._regs, _regs.Length);
            Array.Copy(_floats, copy._floats, _floats.Length);
            return copy;
        }
    }
}

public sealed record DeclareStmt(string Local, PrimitiveKind Type, Expr Value) : Stmt
{
    public string? TypeName { get; init; }
}

public sealed record ExpressionStmt(Expr Value) : Stmt;
