# Progress

Updated 5 November 2026, 12:44 IST (Asia/Calcutta).

## Completed

Milestone 0. The repository started from an empty `main` commit. .NET SDK 9.0.320 was installed locally because it was not on the image. Cpp2IL tag `2022.1.0-pre-release.21` (`12c8b4413900558a14e30adc4fc4d13d82383901`) was inspected. Its ARM64 ISIL path does not reconstruct C#, and `dll_il_recovery` writes `ldnull; throw`. That gap is why method bodies come from `Arm64Recoverer` and Disarm, while LibCpp2IL is limited to metadata and registration lookup.

Milestone 1. `il2cpp-recover inventory` hashes the APK, checks the archive, extracts the IL2CPP inputs, and prints backend, Unity version source, metadata header, and ABI.

Milestone 2. `analyze` reads a synthetic metadata v31 catalog through LibCpp2IL and writes `methods.json`, `metadata-catalog.json`, `native-map.json`, `recovery-report.json`, and a C# file. On the synthetic ELF, LibCpp2IL does not find code registration. The only method stays `MetadataOnly` with `fullyRecoveredUniqueBodies` 0. The placeholder is explicit.

Milestone 3. The lifter covers the shapes in `docs/SUPPORT.md`. Tests compile the emitted C# with Roslyn and call it for wraparound, signed and unsigned compares, a while-loop sum, field get/set, and a direct call. An unsupported word is a placeholder and does not contain `return 0;`. A next-method-address slice stays `PartiallyRecovered`.

Milestone 4, partial. The Avalonia project builds and is wired to `AnalysisPipeline`. The window was not opened.

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

## Verified

- Solution tests on Linux with SDK 9.0.320.
- CLI inventory, analyze, and recover-slice against the synthetic APK and the two-instruction slice above.
- Desktop project compilation.

## Not verified

- Unity Editor 2022.3.62f3 and the Android IL2CPP player build. Blocked: Unity is not installed. `fixtures/Unity2022.3.62f3/BUILD.md` describes the missing step. No end-to-end recovery of a Unity-built `libil2cpp.so` was run, and none is claimed.
- That Unity emits the ARM64 sequences in the tests.
- Windows process isolation, installers, and the Avalonia window.
- Real code-registration parsing, Il2CppType resolution, and metadata-backed field offsets.

## Next implementation step

Build the fixture in `fixtures/Unity2022.3.62f3` with Unity 2022.3.62f3, Android ARM64, IL2CPP, and keep `global-metadata.dat` plus `libil2cpp.so`. Point `analyze` at that APK. For one small method, record the native address LibCpp2IL returns, the file offset, and the bytes. Lift only if those bytes match a supported shape; otherwise add the missing instruction pattern with a new regression test whose expected result comes from the fixture source, and leave the method `Unsupported` until that test exists.
