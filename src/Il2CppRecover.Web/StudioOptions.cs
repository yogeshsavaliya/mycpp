namespace Il2CppRecover.Web;

public sealed class StudioOptions
{
    public required string Url { get; init; }
    public required bool FixedUrl { get; init; }
    public required string DataRoot { get; init; }
    public required string CliDll { get; init; }
    public required string FrontendDist { get; init; }
    public long MaxUploadBytes { get; init; } = 2L * 1024 * 1024 * 1024;

    public static StudioOptions From(IConfiguration configuration, IWebHostEnvironment environment)
    {
        var dataRoot = configuration["Studio:DataRoot"];
        if (string.IsNullOrWhiteSpace(dataRoot))
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(local))
                local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
            dataRoot = Path.Combine(local, "Il2CppRecover", "studio");
        }

        var cli = configuration["Studio:CliDll"];
        if (string.IsNullOrWhiteSpace(cli))
            cli = Path.Combine(AppContext.BaseDirectory, "il2cpp-recover.dll");

        var dist = configuration["Studio:FrontendDist"];
        if (string.IsNullOrWhiteSpace(dist))
        {
            var beside = Path.Combine(AppContext.BaseDirectory, "studio");
            var fromProject = Path.GetFullPath(Path.Combine(environment.ContentRootPath, "..", "..", "web", "studio", "dist"));
            dist = Directory.Exists(beside) ? beside : fromProject;
        }

        return new StudioOptions
        {
            Url = configuration["Studio:Url"] ?? "http://127.0.0.1:5088",
            FixedUrl = !string.Equals(configuration["Studio:FixedUrl"], "false", StringComparison.OrdinalIgnoreCase),
            DataRoot = Path.GetFullPath(dataRoot),
            CliDll = Path.GetFullPath(cli),
            FrontendDist = Path.GetFullPath(dist),
            MaxUploadBytes = 2L * 1024 * 1024 * 1024
        };
    }
}
