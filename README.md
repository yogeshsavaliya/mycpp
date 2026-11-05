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

## IL2CPP Recovery Studio

Local web workbench. The browser talks to `http://127.0.0.1:5088` on this computer. Packages are stored under the local data directory and are not uploaded.

Choose the APK on **Import Package**: drop it on the dashed area or use **Choose File**, then **Start Analysis**.

| Format | Status |
| --- | --- |
| APK | Supported. The existing inventory and analysis pipeline runs on the file. |
| XAPK, APKS, APKM | Supported when the archive contains one compatible ARM64 `libil2cpp.so` and one `global-metadata.dat`. Conflicting or missing splits are rejected. |
| AAB | Not available. The page says bundletool is required and is not included. |
| IPA, WebAssembly, GameAssembly.dll, a standalone `libil2cpp.so` | Not supported. A native binary still needs matching metadata. |

There is no one-click executable in this repository.

### Windows startup

Install the .NET SDK 9 from https://dot.net and Node.js 22 or later from https://nodejs.org. From the repository root in PowerShell:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\start-studio.ps1
```

The script checks for `dotnet`, `node`, and `npm`, builds the interface, starts the server, prints `http://127.0.0.1:5088`, and opens that address. It then prints `Stop-Process -Id <process id>`. Stopping the server does not delete projects. This PowerShell script has not been executed on Windows.

### Linux startup

`curl`, the .NET SDK, and Node.js must be on `PATH`.

```bash
./scripts/start-studio.sh
```

The script prints `http://127.0.0.1:5088` after `/api/health` answers and tries `xdg-open`. Press Ctrl+C in that window to stop.

### Development startup

This is separate from the script above. Run both processes, then open `http://127.0.0.1:5173`. The dev server proxies `/api` to port 5088.

```bash
dotnet run --project src/Il2CppRecover.Web
```

```bash
cd web/studio
npm install
npm run dev
```

## Documents

- `docs/ARCHITECTURE.md` — components and the upstream boundary
- `docs/SUPPORT.md` — support matrix
- `docs/LIMITATIONS.md` — correctness limits
- `docs/LICENSES.md` — pinned packages and their NuGet license expressions
- `docs/PROGRESS.md` — evidence from this session and the next step
