using System.Text.Json;

namespace Il2CppRecover.Analysis;

/// <summary>Append-only JSONL trace. The UI log stays bounded; this file keeps every method boundary.</summary>
public sealed class AnalysisTrace : IDisposable
{
    private readonly StreamWriter _writer;
    private readonly object _gate = new();

    public AnalysisTrace(string path)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);
        _writer = new StreamWriter(new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = true };
    }

    public void Write(string kind, IReadOnlyDictionary<string, object?> fields)
    {
        var payload = new Dictionary<string, object?>(fields)
        {
            ["kind"] = kind,
            ["utc"] = DateTimeOffset.UtcNow.ToString("O")
        };
        var line = JsonSerializer.Serialize(payload);
        lock (_gate)
            _writer.WriteLine(line);
    }

    public void Dispose() => _writer.Dispose();
}
