# Progress

Updated 5 November 2026, 15:40 IST (Asia/Calcutta).

The supplied game APK is still absent. Baseline commit before this pass was `a693b08`. A local Cpp2IL sample, not that APK, was loaded instead: Unity 2022.3.35f1 `GameAssembly.dll` SHA-256 `68220f503869134ab966f9d6c2eee04d98446638f09d8cac0c7a1fe073e90b65`, metadata SHA-256 `2441635a7c234a1a34deccc6a9906d6f5abcbdab078a38b2bf488a52a4962c48`. LibCpp2IL reported 12409 methods, 11989 with addresses, 0 unresolved returns, 2468 types, 8827 fields, and 8812 field offsets. The binary is x64 PE, so no ARM64 body was lifted. Declarations from that load keep `byte[]`, `string`, `bool`, `void`, and generic `Action` spellings. Field offsets are comments, not a C# layout.

Clang 18.1.3 `-O1 -target aarch64-linux-gnu` emitted `add`, `csel` `max`, and `fadd`. Those three now lift and match their source results under Roslyn. The same compiler's `sum_to` is a strength-reduced sequence and stays a placeholder. Ghidra was not run. There is no ARM64 IL2CPP binary here on which to measure it, and its pseudocode is not converted into C#.

```text
dotnet test Il2CppRecover.sln
```

Result: engine tests Passed 58, web tests Passed 7. Solution build 0 warnings.

## Earlier evidence

## Completed

Milestone 0. The repository started from an empty `main` commit. .NET SDK 9.0.320 was installed locally because it was not on the image. Cpp2IL tag `2022.1.0-pre-release.21` (`12c8b4413900558a14e30adc4fc4d13d82383901`) was inspected. Its ARM64 ISIL path does not reconstruct C#, and `dll_il_recovery` writes `ldnull; throw`. That gap is why method bodies come from `Arm64Recoverer` and Disarm, while LibCpp2IL is limited to metadata and registration lookup.

Milestone 1. `il2cpp-recover inventory` hashes the APK, checks the archive, extracts the IL2CPP inputs, and prints backend, Unity version source, metadata header, and ABI.

Milestone 2. `analyze` reads a synthetic metadata v31 catalog through LibCpp2IL and writes `methods.json`, `metadata-catalog.json`, `native-map.json`, `recovery-report.json`, and a C# file. On the synthetic ELF, LibCpp2IL does not find code registration. The only method stays `MetadataOnly` with `fullyRecoveredUniqueBodies` 0. The placeholder is explicit.

Milestone 3. The lifter covers the shapes in `docs/SUPPORT.md`. Tests compile the emitted C# with Roslyn and call it for wraparound, signed and unsigned compares, a while-loop sum, field get/set, and a direct call. An unsupported word is a placeholder and does not contain `return 0;`. A next-method-address slice stays `PartiallyRecovered`.

Milestone 4, partial. The Avalonia project builds and is wired to `AnalysisPipeline`. The window was not opened.

Milestone 5. IL2CPP Recovery Studio is a local web application on the same pipeline. Import stores an APK, a worker process runs `il2cpp-recover worker`, and the results page reads the report. XAPK, APKS, and APKM are normalized by `PackageSetReader` before that pipeline. AAB stays unavailable. The interface text is English.

## Commands actually run

```text
dotnet test Il2CppRecover.sln
```

Result after the file-share fix in extraction and the struct `FirstOrDefault` fix in the lifter: Passed 36, Failed 0, Skipped 0.

```text
dotnet build src/Il2CppRecover.Desktop/Il2CppRecover.Desktop.csproj
```

Result: Build succeeded, 0 warnings, 0 errors. Configuration is Debug. The executable was not started.

```text
dotnet exec il2cpp-recover.dll inventory /tmp/il2cpp-sample/game.apk --output /tmp/il2cpp-sample/inventory
dotnet exec il2cpp-recover.dll analyze /tmp/il2cpp-sample/game.apk --output /tmp/il2cpp-sample/analyze --in-process
dotnet exec il2cpp-recover.dll recover-slice --code /tmp/il2cpp-sample/add.bin --address 1000 --name Add --type ArithmeticFixture --namespace RecoveryFixtures --static --return int32 --params a:int32,b:int32 --output /tmp/il2cpp-sample/slice
```

The APK is a zip built in the test helper: ELF64 `e_machine` 183, synthetic metadata v31, and `globalgamemanagers` containing `2022.3.62f3` at offset `0x14`. It is not a Unity player.

Inventory stdout:

```json
{
  "succeeded": true,
  "scriptingBackend": "il2Cpp",
  "unityVersion": "2022.3.62f3",
  "unitySource": "globalgamemanagers+0x14",
  "metadataVersion": 31,
  "abi": "arm64-v8a",
  "architecture": "Arm64",
  "sha256": "593c3ad707da7e124ac58c18a07015fdc4ea363e47315d58b6aeb59144580207",
  "diagnostics": []
}
```

Analyze counts: `methods` 1, `fullyRecoveredUniqueBodies` 0, `metadataOnly` 1. Engine diagnostic: `Failed to find code registration or metadata registration!` The catalog layout selector logged by LibCpp2IL was `31.1` because the detected Unity string is at least `2022.3.33f1`. The metadata header version in the report stayed 31.

`Game.Player.Jump` signature in `methods.json`: `unresolved Game.Player::Jump(unresolved speed)`, status `metadataOnly`.

`recover-slice` stdout: `RecoveredUnvalidated body`. Emitted method:

```csharp
public static int Add(int a, int b)
{
    unchecked
    {
        return (a + b);
    }
}
```

The bytes were `ADD W0, W0, W1; RET` (`00 00 01 0B`, `C0 03 5F D6`) at address `0x1000`.

```text
dotnet test Il2CppRecover.sln
dotnet build Il2CppRecover.sln
cd web/studio && npm run build
./scripts/start-studio.sh
```

On 5 November 2026, after the web workbench: engine tests Passed 45, web tests Passed 7, solution build 0 warnings. `npm run build` wrote `web/studio/dist`. `./scripts/start-studio.sh` printed `IL2CPP Recovery Studio is ready at http://127.0.0.1:5088` and `/api/health` returned 200. Ctrl+C stopped it. `xdg-open` was not confirmed to open a window. `scripts/start-studio.ps1` was not executed.

Headless Chrome and a visible Chrome window on the same machine opened `http://127.0.0.1:5088`, imported `/tmp/studio-game.apk` (the synthetic ELF, metadata v31, and `globalgamemanagers` fixture, not a Unity player), and showed job state Completed with Limitations, Unity `2022.3.62f3` from `globalgamemanagers+0x14`, assembly size “Not available”, and the notice “Method body not recovered.” The Import Package page listed APK and XAPK/APKS/APKM as supported and AAB as not available yet.

The user's APK and `analysis-export` directory are not in this environment. The screenshot of `CameraShaker.cs` shows the export bug: diagnostics name `System.Collections.IEnumerator` and floating-point signatures, while the declarations are `int`. `OutputWriter` was rewriting every non-primitive and unresolved type to `int`. Declarations now keep the loader's type name, and `unresolved` stays `unresolved`. `TBZ`/`TBNZ` targets inside a slice were discarded, so an in-slice bit test was reported as a branch that leaves the method. Those targets are decoded. A caller-supplied `TBZ` of bit 0 lifts to `(value & 1) == 0` and matches the values 0, 1, 2, and 3. A `TBZ` whose target is outside the slice stays a placeholder. This was not run on the user's APK, and it is not a measurement of that APK's recovery rate.

```text
dotnet test Il2CppRecover.sln
```

Result: engine tests Passed 54, web tests Passed 7.

## Verified

- Solution tests on Linux with SDK 9.0.320.
- CLI inventory, analyze, and recover-slice against the synthetic APK and the two-instruction slice above.
- Desktop project compilation.
- Web host tests: import of a synthetic APK, engine report, placeholder export, invalid uploads, nested-package rejection, cancel, retry, and restart.
- Browser walk of `http://127.0.0.1:5088` on Linux, including the start script reaching health.

## Not verified

- Unity Editor 2022.3.62f3 and the Android IL2CPP player build. Blocked: Unity is not installed. `fixtures/Unity2022.3.62f3/BUILD.md` describes the missing step. No end-to-end recovery of a Unity-built `libil2cpp.so` was run, and none is claimed.
- That Unity emits the ARM64 sequences in the tests.
- Windows process isolation, installers, the Avalonia window, and `scripts/start-studio.ps1`.
- `xdg-open` actually displaying a browser window.
- Recovery accuracy on a Unity-built game. The browser walk used the synthetic APK.
- Real code-registration parsing, Il2CppType resolution, and metadata-backed field offsets.

## Next implementation step

Build the fixture in `fixtures/Unity2022.3.62f3` with Unity 2022.3.62f3, Android ARM64, IL2CPP, and keep `global-metadata.dat` plus `libil2cpp.so`. Point `analyze` at that APK. For one small method, record the native address LibCpp2IL returns, the file offset, and the bytes. Lift only if those bytes match a supported shape; otherwise add the missing instruction pattern with a new regression test whose expected result comes from the fixture source, and leave the method `Unsupported` until that test exists.

## 2026-11-06 17:03 IST — structured lifter, measured on a clang fixture

The 0.3.0 pipeline passed `NextMethodAddressOnly` for every APK method and then demoted every successful lift of that slice to a placeholder. Tail calls and single-instruction thunks were rejected, and a null-check cold block that ends in a noreturn call was not a closed function. Those paths are now handled by `FunctionDiscovery` and `StructuredLifter`. A body is `RecoveredUnvalidated` only when control flow is closed.

`fixtures/Il2CppStyleArm64` was compiled here with clang 18.1.3, target `aarch64-linux-gnu`, and checked against Unicorn 2.1.4. All 11 methods recovered. All 77 vectors matched the native return, fields, and exception kind. SHA-256 of `libfixture.so` is `978fbde755eb5522b3d992cc38468f454106d3ac32c1210c3bed92d6576fc3c3`. This fixture is hand-written C++ in an IL2CPP calling shape. It is not Unity output and not a game.

`dotnet test Il2CppRecover.sln` on SDK 9.0.320 / net9.0: Il2CppRecover.Tests passed 69, Il2CppRecover.Web.Tests passed 7.

The user's APK was not on this machine. The only real Unity IL2CPP binary here is the Cpp2IL sample for 2022.3.35f1, an x64 PE `GameAssembly.dll`. Packaged with its `global-metadata.dat` and `globalgamemanagers`, `analyze` read Unity `2022.3.35f1` from `globalgamemanagers+0x30`, header 31, layout 31.1, and 12,409 methods. The native file is not an ELF, so the loader was not run and `FullyRecoveredUniqueBodies` is 0. No result is claimed for BodypartController, BoosterButtonController, or CameraShaker.
