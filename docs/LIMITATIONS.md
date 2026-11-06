# Limitations

These limits are part of the result, not a temporary disclaimer in front of a broader claim.

1. Metadata names are not recovered method logic. A method with no mapped native address is `MetadataOnly` and its C# is a throwing placeholder.
2. Empty methods and Cpp2IL dummy DLL stubs are not produced as recovered logic. `dll_il_recovery` is not invoked.
3. An unsupported instruction is not replaced with a guessed return. The diagnostic names the instruction or the missing register value.
4. A placeholder exists so a generated file can compile. It is counted in `PlaceholderBodies` and excluded from `FullyRecoveredUniqueBodies`.
5. No language model writes method bodies. There is no separate AI explanation channel in this revision.
6. Roslyn compiling a lifted method, and a test invoking it, applies only to that test's hand-written bytes and contract. The analyze report says behavioural validation was not run for the APK.
7. The ARM64 bytes in `tests/Il2CppRecover.Tests/Helpers/Arm64Asm.cs` are encoded for the lifter's subset. They are not the output of `il2cpp` or of Unity 2022.3.62f3. A passing test does not show that Unity emits that instruction sequence.
8. Field offsets and call targets used by the lifter tests are supplied by the test. The APK pipeline does not yet invent field layouts. An `LDR` whose address is not in the supplied field list is unsupported, with the diagnostic that no pointer was invented.
9. The next method address is an upper bound. A body is emitted when recursive descent proves the control flow is closed. A loop that the legacy matcher bounds only by the next address stays `PartiallyRecovered`.
10. Shared generic implementations are not expanded. A direct thunk is followed through a bounded, cycle-checked chain to a known method entry. An address shared by several metadata methods is ambiguous and is not lifted.
11. Parameter and return types that live in the native Il2CppType table stay `unresolved` when the native loader does not return them. A name the loader does return is written in the C# declaration even when the body is a placeholder. `unresolved` is not replaced with `int`. The synthetic ELF used in tests does not contain registration structures, so LibCpp2IL reports that code registration was not found.
12. Header versions 24 and 27 have several table layouts that depend on the Unity version string. If that string was not read, tables are not parsed.
13. When no Unity version string is detected, header 31 may be parsed with the internal selector string `2022.3.0f1` so the v31 tables can be read. That string is labeled `internal-selector-for-header-31-not-a-detected-unity-version` and is not reported as the game's Unity version. Header 29 uses `2021.3.0f1` the same way. Native registration is still skipped when no version was detected.
14. Obfuscated, encrypted, or stripped metadata is not a supported input.
15. The tool does not execute code from the APK. It also does not restore scenes, prefabs, assets, or script references.
16. Original local names, comments, and a byte-identical rebuild of the Unity project are not goals and are not claimed.
17. Windows execution, installers, and Job Object limits were not run. The desktop executable was compiled on Linux and the window was not opened.
18. `FullyRecoveredUniqueBodies / MethodsInMetadata` is not a quality score. The report prints the numerator definition and leaves compilation and behaviour unchecked unless a specific pass sets them.
19. The web workbench was exercised on Linux with a synthetic APK. That run does not measure recovery of a Unity-built game. `scripts/start-studio.ps1` was not executed on Windows. An Android App Bundle is shown as unavailable until a tested bundletool conversion exists.
20. Scalar floating-point arithmetic, compares, and selects that the fixture and the hand-written slices cover are lifted. Structs such as `Vector3`, `UMULL`, division, and x64 `GameAssembly.dll` bodies are not. A field offset read from the binary is labelled as a native offset and is not a reconstructed C# layout.
21. The null-reference raise helper is identified from call-site structure. Without enough evidence, a method that reaches it stays a candidate and the exported body is still a placeholder.
22. Nested types are declared inside their declaring type and are written `public`. Metadata accessibility of nested types is not read.
