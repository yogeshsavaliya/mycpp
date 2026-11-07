# Recovery audit (0.4.0)

Measured on this machine with .NET SDK 9.0.320, target `net9.0`. The user's APK, `libil2cpp.so`, and `global-metadata.dat` were not available. Nothing here is a measurement of a particular game class.

## What was wrong in 0.3.0

`AnalysisPipeline` always passed `BoundaryEvidence.NextMethodAddressOnly`, and `Arm64Recoverer.Recover` turned every successful lift of that slice into a placeholder. A method from an APK could not be emitted even when its bytes were a supported shape.

Other gaps in that lifter, confirmed in the source and reproduced on the clang fixture:

- A method that is only `b <callee>` was rejected as an unexpanded thunk.
- A tail call (`b <callee>`, including `b <base .ctor>`) was a branch outside the slice.
- An IL2CPP-style null check ends in a noreturn call with no following instruction, so the control-flow graph was not closed.
- A call cleared callee-saved registers. There was no STP/LDP frame. Float arguments were assigned to X registers. The pipeline never filled `CallTargets`.
- A metadata name such as `Outer+Inner` was written with the `+` still in it. Nested types with the same simple name were merged, and an enum with no methods was not declared.

## What this revision does

`A64Decoder` reads the instruction encoding. Disarm is used for the listing text. `FunctionDiscovery` walks from the entry. The next known method entry is an upper bound, not the end of the method. `StructuredLifter` tracks registers and stack slots under AAPCS64, checks that the frame is restored before `RET` and before a tail call, and structures an acyclic graph. `Arm64Recoverer.Recover` emits a verified body only when that walk is closed and every instruction was lifted. An unidentified noreturn helper is stored as `CandidateCSharp` and the exported method stays a throwing placeholder.

`RuntimeHelperInference` labels one noreturn callee as the null-reference raise helper when at least three call sites, and at least 90% of its noreturn sites, match the null-check shape. Two helpers with the same shape are labelled neither.

The pipeline builds one image-wide `CodeSpace` and call map. A shared native address is marked ambiguous. Struct, enum, and unknown parameter or return types are rejected with a reason. They are not coerced to `int`.

## Fixture

`fixtures/Il2CppStyleArm64` is hand-written C++ in the shape of IL2CPP generated code: a hidden `MethodInfo*`, `NullCheck` plus a noreturn raise, and a base constructor call. Built here:

- clang 18.1.3, `--target=aarch64-linux-gnu -O2 -fPIC -fvisibility=hidden -fno-exceptions -fno-rtti -ffreestanding -nostdlib`
- linked with `ld.lld`
- SHA-256 `978fbde755eb5522b3d992cc38468f454106d3ac32c1210c3bed92d6576fc3c3`

`tools/fixture_vectors.py` executed that `.so` in Unicorn 2.1.4 (seed 20261106) and wrote 77 vectors, 14 of which reach the null-reference helper. `Il2CppStyleFixtureTests` recovered all 11 methods and matched every vector: return value, every field, and the exception kind. The same methods recover when the helper has no symbol name and is identified from the three call sites.

This is compiler output of a small C++ file. It is not a Unity player and not the user's game.

## Real Unity binary on this machine

The Cpp2IL sample `GameAssembly.dll` for Unity 2022.3.35f1 is a 6,481,920-byte x64 PE (SHA-256 `68220f503869134ab966f9d6c2eee04d98446638f09d8cac0c7a1fe073e90b65`). Its metadata is header 31, 1,892,268 bytes, SHA-256 `2441635a7c234a1a34deccc6a9906d6f5abcbdab078a38b2bf488a52a4962c48`.

Those three files were packed into a zip the CLI accepts as an APK (`lib/x86_64/libil2cpp.so`, `global-metadata.dat`, `globalgamemanagers`) and passed to `analyze --in-process --abi x86_64` on 2026-11-06 17:03 IST.

- Unity string `2022.3.35f1` was read from `globalgamemanagers+0x30`.
- Metadata tables: 12,409 methods, LibCpp2IL layout selector 31.1. The file header was not rewritten.
- The native file is not an ELF, so the native loader was not run.
- `FullyRecoveredUniqueBodies` is 0. `MetadataOnly` is 12,409. Placeholder bodies: 11,962. Abstract or external: 447. Generic: 191.
- Inventory `Succeeded` is false because the selected binary is not little-endian ARM64 ELF. The CLI therefore exits 3. The report was still written.

No ARM64 `libil2cpp.so` from a Unity player was present, so this run does not measure method-body recovery of a game.

## Tests

`dotnet test Il2CppRecover.sln`: Il2CppRecover.Tests passed 69, Il2CppRecover.Web.Tests passed 7. SDK 9.0.320, `net9.0`.

## Still unrecovered on a real ARM64 player

Value types (`Vector3`, `Quaternion` and other structs), virtual and interface calls, delegates, generic instances, static fields, string literals, allocation, arrays, loops other than the legacy single-while shape, switches, and coroutine or async state machines. Expect most Unity methods to remain placeholders until those are modelled and checked against a Unity-built binary.
