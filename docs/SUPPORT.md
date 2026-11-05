# Support matrix

Rows are independent. A check in one column does not imply the others.

| Area | State in 0.3.0 | Evidence |
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
| Method-body recovery | The shapes below, only when the caller supplies the slice and the parameter contract | `Arm64RecoveryTests` plus Roslyn execution |
| Compilable C# | Emitted statements compile under Roslyn in the tests. That is not behavioural equivalence for an APK | Test project only. Roslyn is not in the engine |
| Behavioural checks | Test-only, on the hand-written slices | 27 recovery tests. The APK report field stays "no behavioural validation was run" |
| Desktop UI | Project builds. Open / analyze / search / export call the engine | `dotnet build` of `Il2CppRecover.Desktop` succeeded. Window not launched |
| Web workbench | Local import, analysis, results, and export on loopback | `Il2CppRecover.Web.Tests` and a headless Chrome walk of `http://127.0.0.1:5088` on Linux. The Windows PowerShell startup script was not executed |
| AAB (`.aab`) | Not available. bundletool is not included and the bundle is not converted | Capabilities API and `PackageSetReaderTests` |

## ARM64 shapes the lifter accepts

Integer AAPCS64 only. Static arguments start in W0/X0. Instance `this` is X0 and arguments start at X1. The integer return is W0 or X0. More than eight integer arguments is unsupported.

| Shape | Status when the slice is caller-supplied and the lift succeeds |
| --- | --- |
| `MOVZ` / `MOVN` / `MOV` and `RET` | `RecoveredUnvalidated` |
| `ADD` `SUB` `MUL` `AND` `ORR` `EOR` without a shift or extend | `RecoveredUnvalidated`, integer math inside `unchecked` |
| `CMP` plus `B.cond`, `CBZ`, `CBNZ`, `TBZ`, or `TBNZ` where both successors return | `RecoveredUnvalidated`. Unsigned conditions use a `uint` or `ulong` compare. `TBZ`/`TBNZ` are bit tests of a lifted integer, and an in-slice target is not reported as leaving the method |
| `CMP` plus `CSEL` and `RET` | `RecoveredUnvalidated` when the condition is one of the lifted compares. Clang 18 `-O1` `max` uses this shape |
| Scalar `FADD` of `S` or `D` registers and `RET` | `RecoveredUnvalidated` for an all-float or mixed AAPCS64 signature whose values are in the lifted registers. Other floating-point instructions stay unrecovered |
| One while-loop: optional prelude, header of `CMP`/`NOP` only, body ending in `B` back to the header, exit ending in `RET` | `RecoveredUnvalidated` |
| `BL` whose target is in the supplied call map | `RecoveredUnvalidated` |
| Unsigned-offset `LDR`/`STR` of a supplied instance field offset, or a static field whose ADRP+ADD address matches | `RecoveredUnvalidated` |

Anything else, including `SDIV`, shifts, pre/post-index memory, indirect branches, branches that leave the slice, generics, and floating-point signatures, stays `Unsupported` or `Failed` with a diagnostic. The exported method throws `NotImplementedException` prefixed with `IL2CPP_RECOVER_UNRECOVERED`. The lifter does not emit `return 0`, `return null`, or an empty body in place of those instructions.

`ADDS`, `SUBS`, and `ANDS` are not treated as flag-setting compares. A direct single-instruction `B` is an unexpanded thunk.

## Later milestones, not in this tree

ARM32, Windows `GameAssembly.dll`, WebGL, AAB conversion, arrays, strings, switches, object construction, virtual and interface calls, value types, boxing, shared generic bodies, delegates, exceptions, and coroutines.
