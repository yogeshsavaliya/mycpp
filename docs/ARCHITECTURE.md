# Architecture

Il2CppRecover is a .NET 9 solution. The analysis engine does not reference Avalonia. A later web service can call the same pipeline.

| Project | Role |
| --- | --- |
| `Il2CppRecover.Core` | Status enums, hashes, JSON settings, report models. No third-party packages. |
| `Il2CppRecover.Engine` | APK inventory, ELF inspection, metadata catalog, pinned LibCpp2IL adapter, ARM64 lifter, exports, worker host. |
| `Il2CppRecover.Cli` | `il2cpp-recover` commands. |
| `Il2CppRecover.Desktop` | Avalonia 11.3.6 window that calls `AnalysisPipeline` on a background thread. |
| `Il2CppRecover.Web` | ASP.NET Core host for IL2CPP Recovery Studio. SQLite stores projects, jobs, and settings. A background service starts `il2cpp-recover worker` in a separate process. |
| `web/studio` | React and TypeScript interface. The production build is served by the web host from the same origin. |
| `Il2CppRecover.Tests` | Synthetic metadata, hand-written ARM64, Roslyn execution of lifted C#, and nested-package cases. |
| `Il2CppRecover.Web.Tests` | Import, analysis, export, cancellation, retry, and restart against the web host. |

## Upstream boundary

Pinned NuGet packages, matching the Cpp2IL tag `2022.1.0-pre-release.21`, commit `12c8b4413900558a14e30adc4fc4d13d82383901`:

- `Samboy063.LibCpp2IL` 2022.1.0-pre-release.21
- `Samboy063.Cpp2IL.Core` 2022.1.0-pre-release.21
- `Disarm` 2022.1.0-master.99
- `AssetRipper.Primitives` 3.2.0

`Cpp2IlSession` calls `LibCpp2IlMain.Initialize` under a lock and copies method pointers when registration is found. It does not call `Cpp2IlApi` and it does not write dummy DLLs.

At that commit, `AsmResolverDllOutputFormatIlRecovery.FillMethodBody` emits `ldnull; throw` for managed methods that have bodies. `NewArmV8InstructionSet` drops intra-method unconditional `B` and reports `CMP` as not implemented. Method bounds often come from the next function start. Those paths are not used as recovered C#.

Disarm renders the instruction listing. Semantics come from `A64Decoder`, which reads the raw A64 encoding. `FunctionDiscovery` walks the method by recursive descent, using the next known method entry only as an upper bound. `StructuredLifter` lifts that function. `Arm64Recoverer.Recover` emits a body only when that walk is closed. The older shape matcher remains as the loop fallback. ILSpy is not integrated: there is no recovered managed IL to decompile. C# is emitted from our statements.

LibCpp2IL keeps process-wide statics (`LibCpp2IlMain.TheMetadata` and friends). Tests disable xUnit parallelization. Do not call the catalog reader and the native loader concurrently.

## Input path

`ApkArchive` opens one APK read-only with `ZipArchive`. It rejects absolute names, drive-like names, `.` and `..` segments, more than 100,000 entries, an entry over 512 MB, a total over 2 GB, and a compression ratio above 100 when the compressed size is at least 64 KB. The original APK is hashed before extraction. Extracted bytes are hashed after the write stream is closed. Calling `ApkArchive` directly on `.xapk`, `.apks`, `.apkm`, or a zip of nested `.apk` files rejects the input.

`PackageSetReader` is the web and CLI-facing normalizer. It detects ELF, PE, WebAssembly, IPA, and AAB by content. An Android App Bundle is reported unavailable because bundletool is not included and no conversion is performed. A zip of nested APKs is unpacked under the same size limits, and each member is scanned. The reader keeps one `global-metadata.dat` and one `libil2cpp.so` for the requested ABI. Identical hashes are noted once. Different hashes are rejected. The normalized APK and `origins.json` record which member each file came from. A plain APK is analyzed from the original file.

Unity version detection reads the C string at `globalgamemanagers` offset `0x14`, then `0x30`, then `data.unity3d` offset `0x12`. A later ASCII scan of the first 2 MB of extracted files is accepted only when it finds one unique match of `^[0-9]+\.[0-9]+\.[0-9]+[abcfxp][0-9]+$`. Several distinct matches leave the version unset. A user-supplied version is stored only when it matches that pattern and either nothing was detected or it equals the detected string.

## Method records

Each method keeps assembly, namespace, declaring type, signature, metadata token and index when known, native virtual address and file offset when mapped, boundary evidence, instructions, diagnostics, placeholder or lifted C#, and provenance ranges when a lift produced statements.

A body becomes `RecoveredUnvalidated` only with `BoundaryEvidence.ControlFlowClosed`: every reachable path ends in `RET`, a tail call to a known method entry, or a call with noreturn evidence, and no path crosses the next known entry. An unidentified noreturn helper stays `PartiallyRecovered`; its text is `CandidateCSharp` and the exported body is a throwing placeholder. The legacy loop path still refuses a slice whose only bound is the next method address.

`FullyRecoveredUniqueBodies` counts distinct native addresses whose status is `RecoveredUnvalidated` and whose C# is not a placeholder. Abstract, external, metadata-only, placeholder, and duplicate shared rows are outside that numerator. `Compilation` and `Behaviour` stay `NotChecked` unless a later pass sets them. The APK pipeline does not set them from a lift.

## Native backend

Ghidra 11 is Apache-2.0 and was not installed for this revision. A headless run was not used as the C# generator. The user's ARM64 APK is not in this environment, so there is no same-binary measurement of Ghidra's boundaries against our lifter. Decompiler pseudocode is not converted into C# with text replacement.

The C# generator is `StructuredLifter` behind `Arm64Recoverer.Recover`. `fixtures/Il2CppStyleArm64` is hand-written C++ in an IL2CPP calling shape, compiled by clang 18.1.3 for `aarch64-linux-gnu`. It is not Unity output. All 11 methods recovered and matched native execution on 77 Unicorn vectors. Clang's strength-reduced `sum_to` uses `SUBS`, `UMULL`, and `LSR` and stays a placeholder.

LibCpp2IL remains the metadata and address source. On the pinned loader, the Cpp2IL sample `GameAssembly.dll` for Unity 2022.3.35f1 (x64 PE, not ARM64) produced resolved signatures and field offsets. Those bodies are not lifted, because the lifter reads ARM64 only.

## Worker

`analyze` writes `worker-request.json` and starts `dotnet exec` on this assembly's `worker` command. `WorkerHost` kills the process tree on timeout (exit 5) or cancellation (exit 6). That was tested on Linux by killing `sleep`. It was not tested on Windows.
