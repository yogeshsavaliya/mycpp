# Support matrix

Rows are independent. A check in one column does not imply the others.

| Area | State in 0.4.0 | Evidence |
| --- | --- | --- |
| Input: Android APK (`.apk`) | Inventory, hash, bounded extraction | `InputAndMetadataTests`, CLI `inventory` on a synthetic APK |
| Input: XAPK, APKS, APKM, nested APK | Inventory of members, then one normalized APK when ARM64 native code and metadata agree | `PackageSetReaderTests`: missing split, conflicting binaries, unsafe names, decompression limit |
| Input: Mono (`Managed/*.dll` and no IL2CPP files) | Detected as Mono. No method recovery | `Mono_and_wrong_architecture_are_explicit` |
| ABI: `arm64-v8a`, ELF64 little-endian AArch64 (`e_machine` 183) | Selected by default and validated | Inventory test |
| ABI: ARM32, x86, x86-64 | Identified. Not selected for recovery. x86-64 does not count as a successful IL2CPP ARM64 input | Architecture test |
| Metadata header 31 with Unity `2022.3.62f3` | Tables parsed. LibCpp2IL's internal selector becomes 31.1 because that Unity string is at least `2022.3.33f1`. The file header stays 31. 31.1 is a code-registration difference in LibCpp2IL, not a rewritten metadata version | Synthetic v31 fixture and analyze log `layout=31.1` |
| Metadata 24 or 27 without a Unity version string | Header reported. Tables not parsed. No version invented | `Header_24_without_a_unity_version_is_not_guessed` |
| Metadata 23–106 | LibCpp2IL `ReadFrom` accepts the range and throws outside it. Layouts other than the synthetic v31 fixture are not covered by our tests | Upstream reader, not our fixture |
| Protected or encrypted metadata | Not claimed. If no version string is present, code registration is not queried | `Protected_metadata_does_not_receive_a_version` |
| Native registration on a real `libil2cpp.so` | Not verified. A synthetic ELF returns LibCpp2IL's "Failed to find code registration or metadata registration" and methods stay `MetadataOnly` | Analyze sample and `Synthetic_elf_does_not_crash_the_pinned_loader` |
| Metadata recovery | Names, tokens, static/instance, abstract/external flags. Parameter and return types stay `unresolved` until the Il2CppType table in the native binary is read | Analyze `methods.json` for `Game.Player.Jump` |
| Disassembly | Disarm text and bytes for a consumed ARM64 slice | Recovery tests |
| Method-body recovery | Structured lifter for acyclic functions whose control flow is closed. See the table below | `Il2CppStyleFixtureTests` (clang AArch64, native-vs-C# vectors) and `Arm64RecoveryTests` |
| Compilable C# | Emitted statements compile under Roslyn in the tests. That is not behavioural equivalence for an APK | Test project only. Roslyn is not in the engine |
| Behavioural checks | Test-only. The APK report field stays "no behavioural validation was run" | Hand-written slices, plus 11 fixture methods matched to 77 Unicorn vectors |
| Desktop UI | Project builds. Open / analyze / search / export call the engine | `dotnet build` of `Il2CppRecover.Desktop` succeeded. Window not launched |
| Web workbench | Local import, analysis, results, and export on loopback | `Il2CppRecover.Web.Tests` and a headless Chrome walk of `http://127.0.0.1:5088` on Linux. The Windows PowerShell startup script was not executed |
| AAB (`.aab`) | Not available. bundletool is not included and the bundle is not converted | Capabilities API and `PackageSetReaderTests` |

## What the structured lifter accepts (0.4.0)

AAPCS64: `this` in X0, integer and reference arguments in X registers, float and double arguments in V registers, hidden `MethodInfo*` ignored, at most eight of each. Returns in W0/X0 or S0/D0.

| Feature | Notes |
| --- | --- |
| Integer ALU | ADD/SUB, AND/ORR/EOR including shifted and inverted forms, MOVZ/MOVN/MOVK, bitfield moves, MADD/MSUB, variable shifts. Division is rejected because AArch64 and C# differ on zero and overflow. |
| Flags and selects | CMP/SUBS and the compares the tests cover, B.cond, CBZ/CBNZ, TBZ/TBNZ, CSEL/CSINC/CSINV/CSNEG. |
| Floating point | FMOV, FADD/FSUB/FMUL/FDIV, FNEG, FABS, FCMP, FCSEL, and the conversions the decoder names. `fixtures/Il2CppStyleArm64` checks `FMUL` of a float field. |
| Memory | Field loads and stores through a reference at a metadata offset, including a wide constant store split across adjacent fields, and a conditionally selected field. Stack save/restore of the frame. |
| Control flow | Acyclic graphs. Tail calls and direct thunks to known method entries. Null checks that end in the identified raise helper. |
| Calls | Direct calls to mapped methods whose parameter and return types are primitives or references. A base constructor call inside `.ctor` becomes a constructor initializer. |

Rejected with a diagnostic: loops other than the legacy single-while fallback, switches, indirect calls, generic instances, struct and enum types, static fields, arrays, strings, allocation, and any instruction the decoder does not recognise. The exported method then throws `NotImplementedException` prefixed with `IL2CPP_RECOVER_UNRECOVERED`. The lifter does not emit `return 0`, `return null`, or an empty body in place of an unlifted instruction.

## Later milestones, not in this tree

ARM32, Windows `GameAssembly.dll`, WebGL, AAB conversion, arrays, strings, switches, object construction, virtual and interface calls, value types, boxing, shared generic bodies, delegates, exceptions, and coroutines.
