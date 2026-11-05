# Licenses

Il2CppRecover's own source in this repository is the application code. Upstream packages are consumed as NuGet dependencies and are not vendored. License expressions below were read from the restored `.nuspec` files (and from `LICENSE.txt` for Iced) on 5 November 2026 (Asia/Calcutta), after `dotnet restore`.

No GPL or other copyleft package is referenced. Il2CppDumper is not used.

## Direct packages

| Package | Version | NuGet license expression | Role |
| --- | --- | --- | --- |
| Samboy063.LibCpp2IL | 2022.1.0-pre-release.21 | MIT | Metadata and binary loading. Copyright © Samboy063 2019-2023 in the nuspec. |
| Samboy063.Cpp2IL.Core | 2022.1.0-pre-release.21 | MIT | Referenced so the pin matches the audited commit. Its output-format writers are not called. |
| Disarm | 2022.1.0-master.99 | MIT | ARM64 disassembly. Authors: Sam Byass (Samboy063). |
| AssetRipper.Primitives | 3.2.0 | MIT | Unity version type used by LibCpp2IL. |
| Avalonia | 11.3.6 | MIT | Desktop UI. |
| Avalonia.Desktop | 11.3.6 | MIT | Desktop UI. |
| Avalonia.Themes.Fluent | 11.3.6 | MIT | Desktop UI. |
| Avalonia.Fonts.Inter | 11.3.6 | MIT | Desktop UI. |
| Microsoft.CodeAnalysis.CSharp | 4.14.0 | MIT | Test-only compilation of lifted C#. Not referenced by the engine. |
| xunit | 2.9.3 | Apache-2.0 | Tests. |
| xunit.runner.visualstudio | 3.1.4 | Apache-2.0 | Tests. |
| Microsoft.NET.Test.Sdk | 17.14.1 | MIT | Tests. |
| Microsoft.Data.Sqlite | 9.0.0 | MIT | Local project and job database for the web workbench. |
| Microsoft.AspNetCore.Mvc.Testing | 9.0.0 | MIT | Web host tests. The ASP.NET Core shared framework is part of the .NET SDK. |

The Cpp2IL commit audited for behaviour is `12c8b4413900558a14e30adc4fc4d13d82383901` (tag `2022.1.0-pre-release.21`) from https://github.com/SamboyCoding/Cpp2IL.

## Transitive packages pulled by Cpp2IL.Core

These are present because of the pin. ARM64 lifting uses Disarm, not Capstone and not Iced.

| Package | Version | NuGet license expression | Note |
| --- | --- | --- | --- |
| AsmResolver | 6.0.0-beta.5 | MIT | |
| AsmResolver.DotNet | 6.0.0-beta.5 | MIT | Used by Cpp2IL's DLL writers, which we do not call. |
| AsmResolver.PE | 6.0.0-beta.5 | MIT | |
| AsmResolver.PE.File | 6.0.0-beta.5 | MIT | |
| AssetRipper.CIL | 1.2.2 | MIT | |
| Iced | 1.21.0 | MIT | `LICENSE.txt`: Copyright (C) 2018-present iced project and contributors. x86/x64 decoder for Cpp2IL. |
| AssetRipper.Gee.External.Capstone | 2.3.2 | MIT | The nuspec expression is MIT. The package also contains native `libcapstone` / `capstone.dll` binaries. Upstream Capstone is BSD-3-Clause. A redistributed build that ships those binaries needs Capstone's copyright and license text. Our ARM64 lifter does not call Capstone. |
| Samboy063.WasmDisassembler | 2022.1.0-pre-release.21 | MIT | WebAssembly path. Not used for Android ARM64. |
| StableNameDotNet | 0.1.0-pre-release.21 | MIT | |

The web interface in `web/studio` uses React 19.2.0, react-router-dom 7.9.4, Vite 7.1.12, and TypeScript 5.9.3. Their npm packages are MIT. `package-lock.json` records the install. No package from that tree is sent a user's APK.

## Other transitive desktop and test packages

Avalonia brings SkiaSharp 2.88.9, HarfBuzzSharp 8.3.1.1, MicroCom.Runtime 0.11.0, Tmds.DBus.Protocol 0.21.2, and Avalonia platform packages, all restored as MIT in their nuspecs except where a native-asset package follows its parent. xUnit brings xunit.abstractions 2.0.3 (Apache-2.0). Newtonsoft.Json 13.0.3 (MIT) is a test-host transitive package.

Redistribution of a published desktop or CLI build must keep the MIT notices for these packages and the BSD notice for any shipped Capstone binary. That packaging step has not been done.
