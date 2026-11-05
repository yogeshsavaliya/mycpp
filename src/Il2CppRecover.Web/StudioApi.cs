using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Il2CppRecover.Web;

public static class StudioApi
{
    private static readonly Regex UnityVersionPattern = new("^[0-9]+\\.[0-9]+\\.[0-9]+[abcfxp][0-9]+$", RegexOptions.Compiled);
    private static readonly Regex AbiPattern = new("^[A-Za-z0-9_-]{1,32}$", RegexOptions.Compiled);

    public static void Map(WebApplication app)
    {
        app.MapGet("/api/health", (StudioOptions options) => Results.Ok(new
        {
            status = "ok",
            url = options.FixedUrl ? options.Url : (string?)null
        }));

        app.MapGet("/api/session", (HttpContext context) =>
        {
            var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
            context.Response.Cookies.Append("studio.token", token, new CookieOptions
            {
                HttpOnly = false,
                SameSite = SameSiteMode.Strict,
                IsEssential = true,
                Secure = false,
                Path = "/"
            });
            return Results.Ok(new { token });
        });

        app.MapGet("/api/capabilities", () => Results.Ok(Capabilities()));

        app.MapGet("/api/dashboard", (StudioDatabase database) =>
        {
            var counts = database.Counts();
            var recent = database.ListProjects(null).Take(5).Select(p => ProjectDto(p, null)).ToList();
            return Results.Ok(new
            {
                projects = counts.Projects,
                runningJobs = counts.Running,
                completedAnalyses = counts.Completed,
                failedAnalyses = counts.Failed,
                recentProjects = recent
            });
        });

        app.MapGet("/api/settings", (StudioDatabase database, StudioOptions options) =>
            Results.Ok(SettingsDto(database.GetSettings(), options)));

        app.MapPut("/api/settings", (SettingsBody? body, StudioDatabase database, StudioOptions options) =>
        {
            body ??= new SettingsBody();
            if (!string.IsNullOrWhiteSpace(body.DefaultAbi) && !AbiPattern.IsMatch(body.DefaultAbi.Trim()))
                return Results.BadRequest(new { errors = new[] { "ABI may contain letters, numbers, hyphens, and underscores." } });
            var saved = database.SaveSettings(body.Theme ?? "light", body.TimeoutSeconds, body.DefaultAbi ?? "arm64-v8a");
            return Results.Ok(SettingsDto(saved, options));
        });

        app.MapGet("/api/projects", (string? q, StudioDatabase database) =>
            Results.Ok(database.ListProjects(q).Select(p => ProjectDto(p, ReadOrigins(p)))));

        app.MapGet("/api/projects/{id}", (string id, StudioDatabase database) =>
        {
            var project = database.GetProject(id);
            if (project is null)
                return Results.NotFound(new { error = "That project is not on this computer." });
            return Results.Ok(ProjectDto(project, ReadOrigins(project)));
        });

        app.MapPost("/api/projects", async (HttpRequest request, StudioDatabase database, StudioOptions options) =>
        {
            if (!request.HasFormContentType)
                return Results.BadRequest(new { errors = new[] { "Choose a package file first." } });

            IFormCollection form;
            try
            {
                form = await request.ReadFormAsync();
            }
            catch (InvalidDataException)
            {
                return Results.BadRequest(new { errors = new[] { "The upload could not be read. Choose the package again." } });
            }

            var file = form.Files.GetFile("file");
            if (file is null || file.Length == 0)
                return Results.BadRequest(new { errors = new[] { "Choose a package file first." } });
            if (file.Length > options.MaxUploadBytes)
                return Results.BadRequest(new { errors = new[] { "This file is larger than the 2 GB upload limit." } });

            var unity = form["unityVersion"].ToString().Trim();
            if (unity.Length > 0 && !UnityVersionPattern.IsMatch(unity))
                return Results.BadRequest(new { errors = new[] { "The Unity version must look like 2022.3.62f3. Leave it empty to keep the version stored in the package." } });

            var abi = form["abi"].ToString().Trim();
            if (abi.Length == 0)
                abi = database.GetSettings().DefaultAbi;
            if (!AbiPattern.IsMatch(abi))
                return Results.BadRequest(new { errors = new[] { "ABI may contain letters, numbers, hyphens, and underscores." } });

            var displayName = form["name"].ToString().Trim();
            var original = Path.GetFileName(file.FileName);
            if (string.IsNullOrWhiteSpace(original) || original is "." or "..")
                original = "package";
            if (original.Length > 180)
                original = original[..180];
            if (displayName.Length == 0)
                displayName = Path.GetFileNameWithoutExtension(original);
            if (displayName.Length > 120)
                displayName = displayName[..120];
            if (displayName.Any(char.IsControl))
                return Results.BadRequest(new { errors = new[] { "The project name contains characters that cannot be shown." } });

            var id = Guid.NewGuid().ToString("N");
            var storage = Path.Combine(options.DataRoot, "projects", id);
            Directory.CreateDirectory(storage);
            var extension = Path.GetExtension(original);
            if (extension.Length is < 2 or > 8 || extension.Any(ch => !char.IsLetterOrDigit(ch) && ch != '.'))
                extension = ".bin";
            var saved = Path.Combine(storage, "upload" + extension.ToLowerInvariant());
            try
            {
                await using (var target = File.Create(saved))
                    await file.CopyToAsync(target);
                var prepared = PackageSetReader.Prepare(saved, Path.Combine(storage, "work"), new ExtractionLimits(), abi);
                if (prepared.State != "ready" || prepared.AnalysisPath is null)
                {
                    Directory.Delete(storage, recursive: true);
                    return Results.BadRequest(new
                    {
                        errors = new[] { prepared.Message ?? "This package could not be imported." },
                        state = prepared.State,
                        kind = prepared.Kind
                    });
                }

                var project = database.InsertProject(new ProjectRecord
                {
                    Id = id,
                    Name = displayName,
                    OriginalFileName = original,
                    SizeBytes = file.Length,
                    Sha256 = Hashing.Sha256File(saved),
                    PackageKind = prepared.Kind,
                    CreatedUtc = DateTime.UtcNow.ToString("O"),
                    StorageDir = storage,
                    AnalysisPath = prepared.AnalysisPath,
                    Notes = string.Join("\n", prepared.Origins?.Notes ?? [])
                });
                var job = Queue(database, project, abi, unity.Length == 0 ? null : unity);
                return Results.Ok(new { project = ProjectDto(project, prepared.Origins), job = JobDto(job) });
            }
            catch (Exception)
            {
                if (Directory.Exists(storage))
                    Directory.Delete(storage, recursive: true);
                return Results.BadRequest(new { errors = new[] { "The package could not be stored. No project was created." } });
            }
        });

        app.MapDelete("/api/projects/{id}", (string id, StudioDatabase database) =>
        {
            var project = database.GetProject(id);
            if (project is null)
                return Results.NotFound(new { error = "That project is not on this computer." });
            if (database.HasOpenJob(id))
                return Results.Conflict(new { error = "Cancel the running analysis before deleting this project." });
            database.DeleteProject(id);
            try
            {
                if (Directory.Exists(project.StorageDir))
                    Directory.Delete(project.StorageDir, recursive: true);
            }
            catch (IOException)
            {
                return Results.Conflict(new { error = "The project record was removed, but some local files are still in use." });
            }
            return Results.Ok(new { deleted = true });
        });

        app.MapPost("/api/projects/{id}/analyze", (string id, AnalyzeBody? body, StudioDatabase database) =>
        {
            body ??= new AnalyzeBody();
            var project = database.GetProject(id);
            if (project is null)
                return Results.NotFound(new { error = "That project is not on this computer." });
            if (database.HasOpenJob(id))
                return Results.Conflict(new { error = "This project already has an analysis running or waiting." });
            var unity = body.UnityVersion?.Trim() ?? "";
            if (unity.Length > 0 && !UnityVersionPattern.IsMatch(unity))
                return Results.BadRequest(new { error = "The Unity version must look like 2022.3.62f3. Leave it empty to keep the version stored in the package." });
            var abi = string.IsNullOrWhiteSpace(body.Abi) ? database.GetSettings().DefaultAbi : body.Abi.Trim();
            if (!AbiPattern.IsMatch(abi))
                return Results.BadRequest(new { error = "ABI may contain letters, numbers, hyphens, and underscores." });
            var job = Queue(database, project, abi, unity.Length == 0 ? null : unity);
            return Results.Ok(JobDto(job));
        });

        app.MapGet("/api/jobs", (StudioDatabase database) => Results.Ok(database.ListJobs().Select(JobDto)));

        app.MapGet("/api/jobs/{id}", (string id, StudioDatabase database) =>
        {
            var job = database.GetJob(id);
            return job is null ? Results.NotFound(new { error = "That job is not on this computer." }) : Results.Ok(JobDto(job));
        });

        app.MapPost("/api/jobs/{id}/cancel", (string id, StudioDatabase database, JobRunner runner) =>
        {
            var job = database.GetJob(id);
            if (job is null)
                return Results.NotFound(new { error = "That job is not on this computer." });
            if (IsTerminal(job.State))
                return Results.Conflict(new { error = "This job has already finished." });
            if (!runner.Cancel(id))
                return Results.Conflict(new { error = "This job could not be cancelled." });
            return Results.Ok(new { cancelled = true });
        });

        app.MapPost("/api/jobs/{id}/retry", (string id, StudioDatabase database) =>
        {
            var job = database.GetJob(id);
            if (job is null)
                return Results.NotFound(new { error = "That job is not on this computer." });
            if (!IsTerminal(job.State))
                return Results.Conflict(new { error = "Wait until the current analysis finishes before retrying." });
            var project = database.GetProject(job.ProjectId);
            if (project is null)
                return Results.NotFound(new { error = "That project is not on this computer." });
            if (database.HasOpenJob(project.Id))
                return Results.Conflict(new { error = "This project already has an analysis running or waiting." });
            var created = Queue(database, project, job.RequestedAbi ?? database.GetSettings().DefaultAbi, job.SuppliedUnityVersion);
            return Results.Ok(JobDto(created));
        });

        app.MapGet("/api/projects/{id}/catalog", (string id, StudioDatabase database) =>
        {
            var project = database.GetProject(id);
            if (project is null)
                return Results.NotFound(new { error = "That project is not on this computer." });
            var job = database.LatestCompletedJob(id);
            if (job is null)
                return Results.Ok(new { assemblies = Array.Empty<object>(), message = "Analysis has not produced a catalog yet." });
            var catalogPath = Path.Combine(job.OutputDir, "metadata-catalog.json");
            if (!File.Exists(catalogPath))
                return Results.Ok(new { assemblies = Array.Empty<object>(), message = "This analysis did not write a metadata catalog." });
            var catalog = JsonSerializer.Deserialize<MetadataCatalog>(File.ReadAllText(catalogPath), JsonDefaults.Options);
            var assemblies = (catalog?.Images ?? []).Select(image => new
            {
                name = image.Name,
                typeCount = image.Types.Count,
                methodCount = image.Types.Sum(t => t.Methods.Count),
                sizeBytes = (long?)null,
                sizeLabel = "Not available"
            });
            return Results.Ok(new
            {
                assemblies,
                sizeExplanation = "Metadata names these assemblies. They are not stored as DLL files in the package, so no file size is shown.",
                scope = "Analyze Project reads the whole package. Assembly-scoped analysis is not available."
            });
        });

        app.MapGet("/api/projects/{id}/methods", (string id, string? q, StudioDatabase database) =>
        {
            if (!TryMethods(database, id, out var methods, out var error))
                return error!;
            var query = q?.Trim() ?? "";
            var matches = methods.Select((method, index) => (method, index)).Where(pair =>
                query.Length == 0
                || pair.method.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                || pair.method.DeclaringType.Contains(query, StringComparison.OrdinalIgnoreCase)
                || pair.method.Namespace.Contains(query, StringComparison.OrdinalIgnoreCase)
                || pair.method.Assembly.Contains(query, StringComparison.OrdinalIgnoreCase)
                || (pair.method.CSharp?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false)
                || (pair.method.CandidateCSharp?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false));
            var list = matches.Take(501).ToList();
            var truncated = list.Count > 500;
            return Results.Ok(new
            {
                truncated,
                methods = list.Take(500).Select(pair => MethodSummary(pair.method, pair.index))
            });
        });

        app.MapGet("/api/projects/{id}/methods/{index:int}", (string id, int index, StudioDatabase database) =>
        {
            if (!TryMethods(database, id, out var methods, out var error))
                return error!;
            if (index < 0 || index >= methods.Length)
                return Results.NotFound(new { error = "That method is not in this analysis." });
            var method = methods[index];
            return Results.Ok(new
            {
                summary = MethodSummary(method, index),
                signature = method.Signature,
                metadataToken = method.MetadataToken,
                nativeVirtualAddress = method.NativeVirtualAddress,
                nativeFileOffset = method.NativeFileOffset,
                boundary = method.BoundaryEvidence.ToString(),
                compilation = method.Compilation,
                behaviour = method.Behaviour,
                diagnostics = method.Diagnostics,
                instructions = method.Instructions,
                csharp = method.CSharp,
                csharpIsPlaceholder = method.CSharpIsPlaceholder,
                candidateCSharp = method.CandidateCSharp,
                provenance = method.Provenance,
                sharedImplementationCount = method.SharedImplementationCount,
                placeholderNotice = method.CSharpIsPlaceholder ? "Method body not recovered." : null,
                validationNotice = "Recovered code has not been checked against the original application’s behaviour."
            });
        });

        app.MapGet("/api/projects/{id}/report", (string id, StudioDatabase database) =>
        {
            var job = database.LatestCompletedJob(id);
            if (job is null)
                return Results.NotFound(new { error = "Analyze this project before opening the report." });
            var path = Path.Combine(job.OutputDir, "recovery-report.json");
            if (!File.Exists(path))
                return Results.NotFound(new { error = "This analysis did not write a recovery report." });
            return Results.Text(File.ReadAllText(path), "application/json");
        });

        app.MapGet("/api/projects/{id}/input", (string id, StudioDatabase database) =>
        {
            var project = database.GetProject(id);
            if (project is null)
                return Results.NotFound(new { error = "That project is not on this computer." });
            var job = database.LatestCompletedJob(id);
            object? inventory = null;
            if (job is not null)
            {
                var path = Path.Combine(job.OutputDir, "inventory.json");
                if (File.Exists(path))
                {
                    try
                    {
                        var parsed = JsonSerializer.Deserialize<InventoryResult>(File.ReadAllText(path), JsonDefaults.Options);
                        if (parsed is not null)
                        {
                            inventory = new
                            {
                                parsed.Succeeded,
                                scriptingBackend = parsed.ScriptingBackend.ToString(),
                                unityVersion = parsed.UnityVersion.Version,
                                unitySource = parsed.UnityVersion.Source,
                                metadataVersion = parsed.Metadata?.HeaderVersion,
                                abi = parsed.SelectedAbi,
                                architecture = parsed.SelectedBinary?.Architecture.ToString(),
                                sha256 = parsed.InputSha256,
                                sizeBytes = parsed.InputSizeBytes,
                                diagnostics = parsed.Diagnostics,
                                files = parsed.ExtractedFiles.Select(file => new { file.ArchivePath, file.Role, file.Sha256, file.SizeBytes })
                            };
                        }
                    }
                    catch (JsonException)
                    {
                        inventory = null;
                    }
                }
            }
            return Results.Ok(new
            {
                project = ProjectDto(project, ReadOrigins(project)),
                inventory
            });
        });

        app.MapGet("/api/projects/{id}/export/{kind}", (string id, string kind, StudioDatabase database) =>
        {
            var job = database.LatestCompletedJob(id);
            if (job is null)
                return Results.NotFound(new { error = "Analyze this project before exporting." });
            var output = Path.GetFullPath(job.OutputDir);
            if (!output.StartsWith(Path.GetFullPath(database.GetProject(id)!.StorageDir) + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                return Results.NotFound(new { error = "The analysis output is not available." });
            return kind switch
            {
                "report" => FileResult(Path.Combine(output, "recovery-report.json"), "application/json", "recovery-report.json"),
                "map" => FileResult(Path.Combine(output, "native-map.json"), "application/json", "native-map.json"),
                "diagnostics" => Diagnostics(output),
                "csharp" => ZipResult(Path.Combine(output, "csharp"), "recovered-csharp.zip", false),
                "full" => ZipResult(output, "analysis-export.zip", true),
                _ => Results.NotFound(new { error = "That export is not available. Choose C#, a JSON report, the address map, diagnostics, or the full analysis export." })
            };
        });
    }

    public static bool OriginAllowed(HttpRequest request)
    {
        if (!request.Headers.TryGetValue("Origin", out var origin) || string.IsNullOrWhiteSpace(origin))
            return true;
        return Uri.TryCreate(origin.ToString(), UriKind.Absolute, out var uri) && uri.Host is "localhost" or "127.0.0.1" or "::1";
    }

    private static JobRecord Queue(StudioDatabase database, ProjectRecord project, string abi, string? unity)
    {
        var id = Guid.NewGuid().ToString("N");
        var output = Path.Combine(project.StorageDir, "jobs", id, "output");
        return database.InsertJob(id, project.Id, output, abi, unity);
    }

    private static bool TryMethods(StudioDatabase database, string id, out MethodRecord[] methods, out IResult? error)
    {
        methods = [];
        error = null;
        if (database.GetProject(id) is null)
        {
            error = Results.NotFound(new { error = "That project is not on this computer." });
            return false;
        }
        var job = database.LatestCompletedJob(id);
        if (job is null)
        {
            error = Results.NotFound(new { error = "Analyze this project before browsing methods." });
            return false;
        }
        var path = Path.Combine(job.OutputDir, "methods.json");
        if (!File.Exists(path))
        {
            error = Results.NotFound(new { error = "This analysis did not write a method list." });
            return false;
        }
        try
        {
            methods = JsonSerializer.Deserialize<MethodRecord[]>(File.ReadAllText(path), JsonDefaults.Options) ?? [];
            return true;
        }
        catch (JsonException)
        {
            error = Results.Json(new { error = "The method list could not be read." }, statusCode: StatusCodes.Status500InternalServerError);
            return false;
        }
    }

    private static object MethodSummary(MethodRecord method, int index) => new
    {
        index,
        assembly = method.Assembly,
        @namespace = method.Namespace,
        declaringType = method.DeclaringType,
        name = method.Name,
        status = method.Status.ToString(),
        statusLabel = StatusLabel(method.Status),
        placeholder = method.CSharpIsPlaceholder,
        isStatic = method.IsStatic
    };

    private static string StatusLabel(MethodRecoveryStatus status) => status switch
    {
        MethodRecoveryStatus.MetadataOnly => "Metadata Only",
        MethodRecoveryStatus.PartiallyRecovered => "Partially Recovered",
        MethodRecoveryStatus.RecoveredUnvalidated => "Recovered — Not Validated",
        MethodRecoveryStatus.Unsupported => "Unsupported",
        MethodRecoveryStatus.Failed => "Failed",
        MethodRecoveryStatus.Disassembled => "Disassembled",
        MethodRecoveryStatus.CompilationChecked => "Compilation Checked",
        MethodRecoveryStatus.BehaviourCheckedOnFixtures => "Behaviour Checked On Fixtures",
        _ => "Unknown"
    };

    private static object ProjectDto(ProjectRecord project, PackageOrigins? origins) => new
    {
        id = project.Id,
        name = project.Name,
        originalFileName = project.OriginalFileName,
        sizeBytes = project.SizeBytes,
        sha256 = project.Sha256,
        packageKind = project.PackageKind,
        createdUtc = project.CreatedUtc,
        unityVersion = project.UnityVersion,
        unitySource = project.UnitySource,
        architecture = project.Architecture,
        metadataVersion = project.MetadataVersion,
        abi = project.Abi,
        notes = project.Notes,
        latestJobId = project.LatestJobId,
        latestState = project.LatestState,
        latestStage = project.LatestStage,
        members = origins?.Members.Select(member => new { member.EntryName, member.SizeBytes, member.Sha256 }) ?? [],
        files = origins?.Files.Select(file => new { file.Role, file.Origin, file.NormalizedPath, file.Sha256, file.SizeBytes }) ?? [],
        scope = "Analyze Project reads the whole package. Assembly-scoped analysis is not available."
    };

    private static object JobDto(JobRecord job) => new
    {
        id = job.Id,
        projectId = job.ProjectId,
        projectName = job.ProjectName,
        state = job.State,
        stateLabel = JobLabel(job.State),
        stage = job.Stage,
        createdUtc = job.CreatedUtc,
        startedUtc = job.StartedUtc,
        finishedUtc = job.FinishedUtc,
        error = job.Error,
        log = job.Log,
        methodDone = job.MethodDone,
        methodTotal = job.MethodTotal,
        progressKnown = job.MethodTotal is > 0
    };

    private static string JobLabel(string state) => state switch
    {
        "Queued" => "Queued",
        "Validating" => "Validating",
        "Extracting" => "Extracting",
        "ReadingMetadata" => "Reading Metadata",
        "AnalyzingNativeCode" => "Analyzing Native Code",
        "GeneratingOutput" => "Generating Output",
        "Completed" => "Completed",
        "CompletedWithLimitations" => "Completed with Limitations",
        "Failed" => "Failed",
        "Cancelled" => "Cancelled",
        _ => state
    };

    private static bool IsTerminal(string state) =>
        state is "Completed" or "CompletedWithLimitations" or "Failed" or "Cancelled";

    private static PackageOrigins? ReadOrigins(ProjectRecord project)
    {
        var path = Path.Combine(project.StorageDir, "work", "origins.json");
        if (!File.Exists(path))
            return null;
        try
        {
            return JsonSerializer.Deserialize<PackageOrigins>(File.ReadAllText(path), JsonDefaults.Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static object SettingsDto(SettingsRecord settings, StudioOptions options) => new
    {
        theme = settings.Theme,
        timeoutSeconds = settings.TimeoutSeconds,
        defaultAbi = settings.DefaultAbi,
        dataDirectory = options.DataRoot,
        bindUrl = options.FixedUrl ? options.Url : null
    };

    private static object Capabilities() => new
    {
        product = "IL2CPP Recovery Studio",
        formats = new object[]
        {
            new { id = "apk", label = "Android APK", extensions = new[] { ".apk" }, state = "supported", detail = "A single Android package. The file is opened as a zip archive and checked for IL2CPP files. The extension alone is not trusted." },
            new { id = "xapk", label = "XAPK, APKS, and APKM", extensions = new[] { ".xapk", ".apks", ".apkm" }, state = "supported", detail = "Nested packages are inventoried. ARM64 native code and metadata must both be present, and conflicting copies are rejected." },
            new { id = "aab", label = "Android App Bundle", extensions = new[] { ".aab" }, state = "unavailable", detail = PackageSetReader.AabMessage }
        },
        notSupported = new[]
        {
            "IPA",
            "WebAssembly",
            "GameAssembly.dll",
            "A standalone libil2cpp.so without global-metadata.dat"
        }
    };

    private static IResult FileResult(string path, string contentType, string downloadName)
    {
        var full = Path.GetFullPath(path);
        if (!File.Exists(full))
            return Results.NotFound(new { error = "That file was not produced by this analysis." });
        return Results.File(full, contentType, downloadName);
    }

    private static IResult Diagnostics(string output)
    {
        var builder = new System.Text.StringBuilder();
        builder.AppendLine("IL2CPP Recovery Studio diagnostics");
        builder.AppendLine("This is an analysis export, not a rebuilt Unity Editor project.");
        var logs = Path.Combine(output, "logs.txt");
        if (File.Exists(logs))
        {
            builder.AppendLine();
            builder.AppendLine("Log");
            builder.AppendLine(File.ReadAllText(logs));
        }
        var methodsPath = Path.Combine(output, "methods.json");
        if (File.Exists(methodsPath))
        {
            var methods = JsonSerializer.Deserialize<MethodRecord[]>(File.ReadAllText(methodsPath), JsonDefaults.Options) ?? [];
            builder.AppendLine();
            builder.AppendLine("Methods");
            foreach (var method in methods)
            {
                builder.AppendLine(method.DeclaringType + "." + method.Name + " — " + method.Status);
                foreach (var diagnostic in method.Diagnostics)
                    builder.AppendLine("  " + diagnostic);
                if (builder.Length > 5_000_000)
                {
                    builder.AppendLine("Diagnostic export stopped after 5 MB.");
                    break;
                }
            }
        }
        var bytes = System.Text.Encoding.UTF8.GetBytes(builder.ToString());
        return Results.File(bytes, "text/plain", "diagnostics.txt");
    }

    private static IResult ZipResult(string directory, string downloadName, bool skipExtracted)
    {
        var root = Path.GetFullPath(directory);
        if (!Directory.Exists(root))
            return Results.NotFound(new { error = "That export was not produced by this analysis." });
        var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                var full = Path.GetFullPath(file);
                var prefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
                if (!full.StartsWith(prefix, StringComparison.Ordinal))
                    continue;
                var relative = full[prefix.Length..].Replace('\\', '/');
                if (skipExtracted && (relative == "extracted" || relative.StartsWith("extracted/", StringComparison.Ordinal)))
                    continue;
                var entry = zip.CreateEntry(relative, CompressionLevel.Fastest);
                using var input = File.OpenRead(full);
                using var output = entry.Open();
                input.CopyTo(output);
            }
        }
        stream.Position = 0;
        return Results.File(stream, "application/zip", downloadName);
    }

    public sealed class SettingsBody
    {
        public string? Theme { get; set; }
        public int TimeoutSeconds { get; set; } = 180;
        public string? DefaultAbi { get; set; }
    }

    public sealed class AnalyzeBody
    {
        public string? Abi { get; set; }
        public string? UnityVersion { get; set; }
    }
}
