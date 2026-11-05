# Il2CppRecover

Local analysis tool for Android Unity IL2CPP APKs. It inventories the package, reads `global-metadata.dat` through a pinned LibCpp2IL build, and lifts a narrow subset of little-endian ARM64 into C#. The engine does not upload the APK.

This is an analysis view. It does not reconstruct a Unity Editor project, scenes, prefabs, or original source comments and names.

## What this revision actually does

- Validates an APK, inventories entries, and extracts `libil2cpp.so`, `global-metadata.dat`, `globalgamemanagers`, and `data.unity3d` with path and size limits.
- Detects Mono versus IL2CPP, ELF architecture, metadata magic `0xFAB11BAF`, and a Unity version string when one is present. A supplied `--unity-version` does not replace a different string read from the file.
- Parses metadata tables for header versions LibCpp2IL accepts when a Unity version string selects a known layout. Header 24 and 27 without a detected version are reported and not guessed.
- Locates method pointers with pinned LibCpp2IL only when that loader finds code and metadata registration. The Cpp2IL `dll_il_recovery` writer is not called. It emits `ldnull` / `throw` stubs, which are not recovered method logic.
- Lifts hand-checked ARM64 shapes: constant returns, integer add/sub/mul/and/orr/eor, signed and unsigned compares, if/else returns, one while-loop shape, direct calls with a supplied target, and field accesses with a supplied offset or absolute address.
- Exports JSON reports, address maps, diagnostics, and C#. Unrecovered methods are throwing placeholders marked `IL2CPP_RECOVER_UNRECOVERED` and are not counted as recovered bodies.

## What it does not do

- It does not recover arbitrary IL2CPP method bodies, generics, floating point, switches, exceptions, or delegates.
- It does not claim a Unity 2022.3.62f3 player was built or recovered here. Unity Editor is not installed in the environment that produced this tree. See `fixtures/Unity2022.3.62f3/BUILD.md`.
- Compilation of emitted C# is not evidence that the C# matches the original game.

## Build

Requires the .NET SDK pinned in `global.json` (9.0.320, latest feature band allowed).

```bash
dotnet test Il2CppRecover.sln
dotnet run --project src/Il2CppRecover.Cli -- version
```

## CLI

```text
il2cpp-recover inventory <apk> --output <dir> [--abi arm64-v8a] [--unity-version 2022.3.62f3]
il2cpp-recover analyze <apk> --output <dir> [--timeout 180] [--in-process]
il2cpp-recover recover-slice --code <bin> --address <hex> --name <method> --type <type> --output <dir>
              [--namespace ...] [--static] [--return int32] [--params a:int32,b:int32]
```

`analyze` runs in a child process unless `--in-process` is set. The child is killed on timeout or cancellation. Windows Job Objects are not implemented. Exit codes: 0 ok, 2 usage, 3 input, 4 analysis, 5 timeout, 6 cancelled.

The desktop project `src/Il2CppRecover.Desktop` is an Avalonia shell over the same pipeline. It was built on Linux. The window was not launched, and Windows packaging was not run.

## Documents

- `docs/ARCHITECTURE.md` — components and the upstream boundary
- `docs/SUPPORT.md` — support matrix
- `docs/LIMITATIONS.md` — correctness limits
- `docs/LICENSES.md` — pinned packages and their NuGet license expressions
- `docs/PROGRESS.md` — evidence from this session and the next step
