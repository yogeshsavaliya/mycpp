# Unity 2022.3.62f3 fixture

The scripts in `Assets/Scripts` are the source we want a future Android IL2CPP player to compile. They were not compiled by Unity in this environment. Unity Editor is not installed, so there is no `libil2cpp.so`, no `global-metadata.dat` from Unity, and no APK. End-to-end recovery against this fixture is blocked.

The hand-written ARM64 tests in `tests/Il2CppRecover.Tests` exercise the same observable results for a subset of these methods. Those tests are not evidence that `il2cpp` emits those encodings.

## Build when Unity 2022.3.62f3 is available

1. Install Unity 2022.3.62f3 and the Android Build Support module with the ARM64 player.
2. Create a project with this `Assets/Scripts` folder, or copy the scripts into an existing 2022.3 project. Do not change the method bodies before the first capture.
3. Player settings: Scripting Backend IL2CPP, Target Architectures ARM64, API Compatibility Level .NET Standard 2.1 or .NET Framework as shipped with 2022.3. Produce one Development build with Managed Stripping Level Minimal, and one with stripping High, so the two outputs can be compared later.
4. Do not upload the APK. Copy `libil2cpp.so` from `lib/arm64-v8a` and `global-metadata.dat` from `assets/bin/Data/Managed/Metadata`. Record SHA-256 for the APK and both files.
5. Run `il2cpp-recover analyze <apk> --output <dir>` and keep `recovery-report.json`. A method is recovered only when its status is `RecoveredUnvalidated` and the C# matches the behaviour of the script below on the values listed in the tests. A placeholder is an unrecovered method.

Il2Cpp version string inside a 2022.3.62f3 player is expected to be that version. If the file contains a different string, keep the file's string.
