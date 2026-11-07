using Microsoft.Data.Sqlite;

namespace Il2CppRecover.Web;

public sealed class ProjectRecord
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string OriginalFileName { get; init; }
    public long SizeBytes { get; init; }
    public required string Sha256 { get; init; }
    public required string PackageKind { get; init; }
    public required string CreatedUtc { get; init; }
    public required string StorageDir { get; init; }
    public required string AnalysisPath { get; init; }
    public string? UnityVersion { get; init; }
    public string? UnitySource { get; init; }
    public string? Architecture { get; init; }
    public int? MetadataVersion { get; init; }
    public string? Abi { get; init; }
    public string Notes { get; init; } = "";
    public string? LatestJobId { get; init; }
    public string? LatestState { get; init; }
    public string? LatestStage { get; init; }
}

public sealed class JobRecord
{
    public required string Id { get; init; }
    public required string ProjectId { get; init; }
    public required string ProjectName { get; init; }
    public required string State { get; init; }
    public string? Stage { get; init; }
    public required string CreatedUtc { get; init; }
    public string? StartedUtc { get; init; }
    public string? FinishedUtc { get; init; }
    public int? ExitCode { get; init; }
    public string? Error { get; init; }
    public string Log { get; init; } = "";
    public required string OutputDir { get; init; }
    public string? RequestedAbi { get; init; }
    public string? SuppliedUnityVersion { get; init; }
    public int? MethodDone { get; init; }
    public int? MethodTotal { get; init; }
}

public sealed class SettingsRecord
{
    public required string Theme { get; init; }
    public int TimeoutSeconds { get; init; }
    public required string DefaultAbi { get; init; }
    public string BodyScope { get; init; } = "full";
}

public sealed class StudioDatabase
{
    private readonly string _connectionString;
    private readonly Lock _gate = new();

    public StudioDatabase(StudioOptions options)
    {
        Directory.CreateDirectory(options.DataRoot);
        var path = Path.Combine(options.DataRoot, "studio.db");
        _connectionString = new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWriteCreate, Cache = SqliteCacheMode.Shared }.ToString();
        With(connection =>
        {
            Execute(connection, """
                CREATE TABLE IF NOT EXISTS projects (
                    id TEXT PRIMARY KEY,
                    name TEXT NOT NULL,
                    original_file_name TEXT NOT NULL,
                    size_bytes INTEGER NOT NULL,
                    sha256 TEXT NOT NULL,
                    package_kind TEXT NOT NULL,
                    created_utc TEXT NOT NULL,
                    storage_dir TEXT NOT NULL,
                    analysis_path TEXT NOT NULL,
                    unity_version TEXT,
                    unity_source TEXT,
                    architecture TEXT,
                    metadata_version INTEGER,
                    abi TEXT,
                    notes TEXT NOT NULL DEFAULT ''
                );
                CREATE TABLE IF NOT EXISTS jobs (
                    id TEXT PRIMARY KEY,
                    project_id TEXT NOT NULL,
                    state TEXT NOT NULL,
                    stage TEXT,
                    created_utc TEXT NOT NULL,
                    started_utc TEXT,
                    finished_utc TEXT,
                    exit_code INTEGER,
                    error TEXT,
                    log TEXT NOT NULL DEFAULT '',
                    output_dir TEXT NOT NULL,
                    requested_abi TEXT,
                    supplied_unity_version TEXT,
                    method_done INTEGER,
                    method_total INTEGER
                );
                CREATE TABLE IF NOT EXISTS settings (
                    id INTEGER PRIMARY KEY CHECK (id = 1),
                    theme TEXT NOT NULL,
                    timeout_seconds INTEGER NOT NULL,
                    default_abi TEXT NOT NULL
                );
                INSERT OR IGNORE INTO settings (id, theme, timeout_seconds, default_abi) VALUES (1, 'light', 180, 'arm64-v8a');
                """);
            EnsureColumn(connection, "settings", "body_scope", "TEXT NOT NULL DEFAULT 'full'");
            return 0;
        });
    }

    private static void EnsureColumn(SqliteConnection connection, string table, string column, string definition)
    {
        using var info = connection.CreateCommand();
        info.CommandText = "PRAGMA table_info(" + table + ");";
        using var reader = info.ExecuteReader();
        while (reader.Read())
        {
            if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
                return;
        }
        using var alter = connection.CreateCommand();
        alter.CommandText = "ALTER TABLE " + table + " ADD COLUMN " + column + " " + definition + ";";
        alter.ExecuteNonQuery();
    }

    public ProjectRecord InsertProject(ProjectRecord project)
    {
        With(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO projects (id, name, original_file_name, size_bytes, sha256, package_kind, created_utc, storage_dir, analysis_path, notes)
                VALUES ($id, $name, $file, $size, $sha, $kind, $created, $dir, $analysis, $notes);
                """;
            Add(command, "$id", project.Id);
            Add(command, "$name", project.Name);
            Add(command, "$file", project.OriginalFileName);
            Add(command, "$size", project.SizeBytes);
            Add(command, "$sha", project.Sha256);
            Add(command, "$kind", project.PackageKind);
            Add(command, "$created", project.CreatedUtc);
            Add(command, "$dir", project.StorageDir);
            Add(command, "$analysis", project.AnalysisPath);
            Add(command, "$notes", project.Notes);
            command.ExecuteNonQuery();
            return 0;
        });
        return GetProject(project.Id)!;
    }

    public IReadOnlyList<ProjectRecord> ListProjects(string? query)
    {
        var like = "%" + (query ?? "").Replace("%", "", StringComparison.Ordinal).Replace("_", "", StringComparison.Ordinal).Trim() + "%";
        return With(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT p.*, j.id AS job_id, j.state AS job_state, j.stage AS job_stage
                FROM projects p
                LEFT JOIN jobs j ON j.id = (SELECT id FROM jobs WHERE project_id = p.id ORDER BY created_utc DESC LIMIT 1)
                WHERE $q = '%%' OR p.name LIKE $q OR p.original_file_name LIKE $q
                ORDER BY p.created_utc DESC;
                """;
            Add(command, "$q", string.IsNullOrWhiteSpace(query) ? "%%" : like);
            return ReadProjects(command);
        });
    }

    public ProjectRecord? GetProject(string id) => With(connection =>
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT p.*, j.id AS job_id, j.state AS job_state, j.stage AS job_stage
            FROM projects p
            LEFT JOIN jobs j ON j.id = (SELECT id FROM jobs WHERE project_id = p.id ORDER BY created_utc DESC LIMIT 1)
            WHERE p.id = $id;
            """;
        Add(command, "$id", id);
        return ReadProjects(command).FirstOrDefault();
    });

    public void UpdateProjectSummary(string id, string? unityVersion, string? unitySource, string? architecture, int? metadataVersion, string? abi)
    {
        With(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE projects
                SET unity_version = $version, unity_source = $source, architecture = $arch, metadata_version = $meta, abi = $abi
                WHERE id = $id;
                """;
            Add(command, "$version", unityVersion);
            Add(command, "$source", unitySource);
            Add(command, "$arch", architecture);
            Add(command, "$meta", metadataVersion);
            Add(command, "$abi", abi);
            Add(command, "$id", id);
            command.ExecuteNonQuery();
            return 0;
        });
    }

    public bool HasOpenJob(string projectId) => With(connection =>
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM jobs WHERE project_id = $id AND state IN ('Queued','Validating','Extracting','ReadingMetadata','AnalyzingNativeCode','GeneratingOutput');";
        Add(command, "$id", projectId);
        return Convert.ToInt32(command.ExecuteScalar()) > 0;
    });

    public void DeleteProject(string id)
    {
        With(connection =>
        {
            using var jobs = connection.CreateCommand();
            jobs.CommandText = "DELETE FROM jobs WHERE project_id = $id;";
            Add(jobs, "$id", id);
            jobs.ExecuteNonQuery();
            using var projects = connection.CreateCommand();
            projects.CommandText = "DELETE FROM projects WHERE id = $id;";
            Add(projects, "$id", id);
            projects.ExecuteNonQuery();
            return 0;
        });
    }

    public JobRecord InsertJob(string id, string projectId, string outputDir, string? abi, string? unityVersion)
    {
        var created = Now();
        With(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO jobs (id, project_id, state, stage, created_utc, output_dir, requested_abi, supplied_unity_version, log)
                VALUES ($id, $project, 'Queued', 'Waiting to start', $created, $output, $abi, $unity, '');
                """;
            Add(command, "$id", id);
            Add(command, "$project", projectId);
            Add(command, "$created", created);
            Add(command, "$output", outputDir);
            Add(command, "$abi", abi);
            Add(command, "$unity", unityVersion);
            command.ExecuteNonQuery();
            return 0;
        });
        return GetJob(id)!;
    }

    public IReadOnlyList<JobRecord> ListJobs() => With(connection =>
    {
        using var command = connection.CreateCommand();
        command.CommandText = JobSelect + " ORDER BY j.created_utc DESC;";
        return ReadJobs(command);
    });

    public JobRecord? GetJob(string id) => With(connection =>
    {
        using var command = connection.CreateCommand();
        command.CommandText = JobSelect + " WHERE j.id = $id;";
        Add(command, "$id", id);
        return ReadJobs(command).FirstOrDefault();
    });

    public JobRecord? LatestCompletedJob(string projectId) => With(connection =>
    {
        using var command = connection.CreateCommand();
        command.CommandText = JobSelect + " WHERE j.project_id = $id AND j.state IN ('Completed','CompletedWithLimitations') ORDER BY j.created_utc DESC LIMIT 1;";
        Add(command, "$id", projectId);
        return ReadJobs(command).FirstOrDefault();
    });

    public JobRecord? TryClaim() => With(connection =>
    {
        using var transaction = connection.BeginTransaction();
        using var select = connection.CreateCommand();
        select.Transaction = transaction;
        select.CommandText = "SELECT id FROM jobs WHERE state = 'Queued' ORDER BY created_utc LIMIT 1;";
        var id = select.ExecuteScalar() as string;
        if (id is null)
        {
            transaction.Commit();
            return null;
        }
        using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = "UPDATE jobs SET state = 'Validating', stage = 'Checking the package', started_utc = $now WHERE id = $id AND state = 'Queued';";
        Add(update, "$now", Now());
        Add(update, "$id", id);
        update.ExecuteNonQuery();
        transaction.Commit();
        using var read = connection.CreateCommand();
        read.CommandText = JobSelect + " WHERE j.id = $id;";
        Add(read, "$id", id);
        return ReadJobs(read).FirstOrDefault();
    });

    public void MarkProgress(string id, string state, string stage, int? methodDone, int? methodTotal, string? logLine)
    {
        With(connection =>
        {
            var log = logLine is null ? null : TrimLog(ExistingLog(connection, id) + logLine + "\n");
            using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE jobs
                SET state = $state, stage = $stage, method_done = $done, method_total = $total, log = COALESCE($log, log)
                WHERE id = $id;
                """;
            Add(command, "$state", state);
            Add(command, "$stage", stage);
            Add(command, "$done", methodDone);
            Add(command, "$total", methodTotal);
            Add(command, "$log", log);
            Add(command, "$id", id);
            command.ExecuteNonQuery();
            return 0;
        });
    }

    public void Finish(string id, string state, string stage, int? exitCode, string? error)
    {
        With(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE jobs
                SET state = $state, stage = $stage, exit_code = $code, error = $error, finished_utc = $now
                WHERE id = $id;
                """;
            Add(command, "$state", state);
            Add(command, "$stage", stage);
            Add(command, "$code", exitCode);
            Add(command, "$error", error);
            Add(command, "$now", Now());
            Add(command, "$id", id);
            command.ExecuteNonQuery();
            return 0;
        });
    }

    public bool TryCancelQueued(string id)
    {
        return With(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE jobs
                SET state = 'Cancelled', stage = 'Cancelled before it started', finished_utc = $now, error = 'Cancelled while waiting.'
                WHERE id = $id AND state = 'Queued';
                """;
            Add(command, "$now", Now());
            Add(command, "$id", id);
            return command.ExecuteNonQuery() == 1;
        });
    }

    public int FailInterrupted() => With(connection =>
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE jobs
            SET state = 'Failed',
                stage = 'Stopped',
                finished_utc = $now,
                error = 'The server stopped while this job was running. The package is still stored. Retry the analysis.'
            WHERE state IN ('Validating','Extracting','ReadingMetadata','AnalyzingNativeCode','GeneratingOutput');
            """;
        Add(command, "$now", Now());
        return command.ExecuteNonQuery();
    });

    public (int Projects, int Running, int Completed, int Failed) Counts() => With(connection =>
    {
        int Scalar(string sql)
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            return Convert.ToInt32(command.ExecuteScalar());
        }
        return (
            Scalar("SELECT COUNT(*) FROM projects;"),
            Scalar("SELECT COUNT(*) FROM jobs WHERE state IN ('Queued','Validating','Extracting','ReadingMetadata','AnalyzingNativeCode','GeneratingOutput');"),
            Scalar("SELECT COUNT(*) FROM jobs WHERE state IN ('Completed','CompletedWithLimitations');"),
            Scalar("SELECT COUNT(*) FROM jobs WHERE state = 'Failed';")
        );
    });

    public SettingsRecord GetSettings() => With(connection =>
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT theme, timeout_seconds, default_abi, body_scope FROM settings WHERE id = 1;";
        using var reader = command.ExecuteReader();
        reader.Read();
        return new SettingsRecord
        {
            Theme = reader.GetString(0),
            TimeoutSeconds = reader.GetInt32(1),
            DefaultAbi = reader.GetString(2),
            BodyScope = reader.IsDBNull(3) ? "full" : reader.GetString(3)
        };
    });

    public SettingsRecord SaveSettings(string theme, int timeoutSeconds, string defaultAbi, string? bodyScope = null)
    {
        if (theme is not ("light" or "dark"))
            theme = "light";
        if (timeoutSeconds < 10)
            timeoutSeconds = 10;
        if (timeoutSeconds > 3600)
            timeoutSeconds = 3600;
        if (string.IsNullOrWhiteSpace(defaultAbi))
            defaultAbi = "arm64-v8a";
        if (bodyScope is not ("full" or "gameplay"))
            bodyScope = "full";
        With(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE settings SET theme = $theme, timeout_seconds = $timeout, default_abi = $abi, body_scope = $scope WHERE id = 1;";
            Add(command, "$theme", theme);
            Add(command, "$timeout", timeoutSeconds);
            Add(command, "$abi", defaultAbi.Trim());
            Add(command, "$scope", bodyScope);
            command.ExecuteNonQuery();
            return 0;
        });
        return GetSettings();
    }

    private const string JobSelect = """
        SELECT j.id, j.project_id, p.name, j.state, j.stage, j.created_utc, j.started_utc, j.finished_utc, j.exit_code, j.error, j.log, j.output_dir, j.requested_abi, j.supplied_unity_version, j.method_done, j.method_total
        FROM jobs j JOIN projects p ON p.id = j.project_id
        """;

    private static List<ProjectRecord> ReadProjects(SqliteCommand command)
    {
        var list = new List<ProjectRecord>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new ProjectRecord
            {
                Id = reader.GetString(reader.GetOrdinal("id")),
                Name = reader.GetString(reader.GetOrdinal("name")),
                OriginalFileName = reader.GetString(reader.GetOrdinal("original_file_name")),
                SizeBytes = reader.GetInt64(reader.GetOrdinal("size_bytes")),
                Sha256 = reader.GetString(reader.GetOrdinal("sha256")),
                PackageKind = reader.GetString(reader.GetOrdinal("package_kind")),
                CreatedUtc = reader.GetString(reader.GetOrdinal("created_utc")),
                StorageDir = reader.GetString(reader.GetOrdinal("storage_dir")),
                AnalysisPath = reader.GetString(reader.GetOrdinal("analysis_path")),
                UnityVersion = Optional(reader, "unity_version"),
                UnitySource = Optional(reader, "unity_source"),
                Architecture = Optional(reader, "architecture"),
                MetadataVersion = reader.IsDBNull(reader.GetOrdinal("metadata_version")) ? null : reader.GetInt32(reader.GetOrdinal("metadata_version")),
                Abi = Optional(reader, "abi"),
                Notes = reader.GetString(reader.GetOrdinal("notes")),
                LatestJobId = Optional(reader, "job_id"),
                LatestState = Optional(reader, "job_state"),
                LatestStage = Optional(reader, "job_stage")
            });
        }
        return list;
    }

    private static List<JobRecord> ReadJobs(SqliteCommand command)
    {
        var list = new List<JobRecord>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new JobRecord
            {
                Id = reader.GetString(0),
                ProjectId = reader.GetString(1),
                ProjectName = reader.GetString(2),
                State = reader.GetString(3),
                Stage = reader.IsDBNull(4) ? null : reader.GetString(4),
                CreatedUtc = reader.GetString(5),
                StartedUtc = reader.IsDBNull(6) ? null : reader.GetString(6),
                FinishedUtc = reader.IsDBNull(7) ? null : reader.GetString(7),
                ExitCode = reader.IsDBNull(8) ? null : reader.GetInt32(8),
                Error = reader.IsDBNull(9) ? null : reader.GetString(9),
                Log = reader.IsDBNull(10) ? "" : reader.GetString(10),
                OutputDir = reader.GetString(11),
                RequestedAbi = reader.IsDBNull(12) ? null : reader.GetString(12),
                SuppliedUnityVersion = reader.IsDBNull(13) ? null : reader.GetString(13),
                MethodDone = reader.IsDBNull(14) ? null : reader.GetInt32(14),
                MethodTotal = reader.IsDBNull(15) ? null : reader.GetInt32(15)
            });
        }
        return list;
    }

    private static string? ExistingLog(SqliteConnection connection, string id)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT log FROM jobs WHERE id = $id;";
        Add(command, "$id", id);
        return command.ExecuteScalar() as string ?? "";
    }

    private static string TrimLog(string log) => log.Length <= 16000 ? log : log[^16000..];

    private static string? Optional(SqliteDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);
        return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    }

    private static void Add(SqliteCommand command, string name, object? value) =>
        command.Parameters.AddWithValue(name, value ?? DBNull.Value);

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private T With<T>(Func<SqliteConnection, T> action)
    {
        lock (_gate)
        {
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();
            using var pragma = connection.CreateCommand();
            pragma.CommandText = "PRAGMA busy_timeout = 5000;";
            pragma.ExecuteNonQuery();
            return action(connection);
        }
    }

    private static string Now() => DateTime.UtcNow.ToString("O");
}
