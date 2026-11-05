using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Il2CppRecover.Analysis;

namespace Il2CppRecover.Desktop;

public partial class MainWindow : Window
{
    private string? _apkPath;
    private AnalysisOutput? _output;
    private List<MethodRecord> _visible = [];

    public MainWindow()
    {
        InitializeComponent();
        AnalyzeButton.IsEnabled = false;
        ExportButton.IsEnabled = false;
    }

    private async void OpenApk(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open an Android APK",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("Android package") { Patterns = ["*.apk"] }]
        });
        var file = files.FirstOrDefault();
        if (file is null)
            return;
        _apkPath = file.Path.LocalPath;
        _output = null;
        AnalyzeButton.IsEnabled = true;
        ExportButton.IsEnabled = false;
        StatusText.Text = _apkPath;
        SummaryText.Text = "APK selected. Analyze runs the local engine. Nothing is uploaded.";
        MethodList.ItemsSource = null;
        CodeBox.Text = "";
        NativeBox.Text = "";
    }

    private async void Analyze(object? sender, RoutedEventArgs e)
    {
        if (_apkPath is null)
            return;
        AnalyzeButton.IsEnabled = false;
        var output = Path.Combine(Path.GetTempPath(), "il2cpprecover-ui-" + Guid.NewGuid().ToString("N"));
        var progress = new Progress<ProgressEvent>(p => LogBox.Text += p.Stage + (p.Detail is null ? "" : ": " + p.Detail) + Environment.NewLine);
        try
        {
            _output = await Task.Run(() => AnalysisPipeline.Analyze(_apkPath, output, new AnalysisOptions(), progress, CancellationToken.None));
            SummaryText.Text = string.Join(Environment.NewLine, new[]
            {
                "Unity version: " + (_output.Inventory.UnityVersion.Version ?? "(not found)") + " from " + (_output.Inventory.UnityVersion.Source ?? "none"),
                "ABI: " + (_output.Inventory.SelectedAbi ?? "(none)") + ", architecture: " + (_output.Inventory.SelectedBinary?.Architecture.ToString() ?? "(none)"),
                "Metadata header: " + (_output.Inventory.Metadata?.HeaderVersion?.ToString() ?? "(none)"),
                "Input SHA-256: " + _output.Inventory.InputSha256,
                "Methods in metadata: " + _output.Report.Counts.MethodsInMetadata,
                "Fully recovered unique bodies: " + _output.Report.Counts.FullyRecoveredUniqueBodies,
                "Metadata only: " + _output.Report.Counts.MetadataOnly,
                "Placeholders: " + _output.Report.Counts.PlaceholderBodies,
                _output.EngineLoadDiagnostic ?? ""
            });
            ShowMethods(_output.Methods);
            ExportButton.IsEnabled = true;
            StatusText.Text = "Output: " + _output.OutputDirectory;
        }
        catch (Exception ex)
        {
            SummaryText.Text = ex.Message;
        }
        finally
        {
            AnalyzeButton.IsEnabled = true;
        }
    }

    private async void Export(object? sender, RoutedEventArgs e)
    {
        if (_output is null)
            return;
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Export folder", AllowMultiple = false });
        var folder = folders.FirstOrDefault();
        if (folder is null)
            return;
        var destination = folder.Path.LocalPath;
        foreach (var file in Directory.EnumerateFiles(_output.OutputDirectory))
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: true);
        var code = Path.Combine(_output.OutputDirectory, "csharp");
        if (Directory.Exists(code))
            CopyDirectory(code, Path.Combine(destination, "csharp"));
        StatusText.Text = "Exported to " + destination;
    }

    private void SearchChanged(object? sender, TextChangedEventArgs e)
    {
        if (_output is null)
            return;
        ShowMethods(_output.Methods);
    }

    private void MethodSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (MethodList.SelectedItem is not MethodRow row)
            return;
        var method = row.Method;
        var code = method.Status == MethodRecoveryStatus.RecoveredUnvalidated ? method.CSharp : method.CandidateCSharp ?? method.CSharp;
        CodeBox.Text = (method.CSharpIsPlaceholder ? "PLACEHOLDER / UNRECOVERED\n" : "RECOVERED, NOT BEHAVIOUR-CHECKED FOR THIS FILE\n") + (code ?? "");
        NativeBox.Text = method.Status + " / " + method.BoundaryEvidence + Environment.NewLine
            + string.Join(Environment.NewLine, method.Diagnostics)
            + Environment.NewLine
            + string.Join(Environment.NewLine, method.Instructions.Select(i => i.Address + "  " + i.Bytes + "  " + i.Text));
    }

    private void ShowMethods(IReadOnlyList<MethodRecord> methods)
    {
        var query = SearchBox.Text?.Trim() ?? "";
        _visible = methods.Where(m => query.Length == 0
            || m.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
            || m.DeclaringType.Contains(query, StringComparison.OrdinalIgnoreCase)
            || (m.CSharp?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false)
            || (m.CandidateCSharp?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false)).ToList();
        MethodList.ItemsSource = _visible.Select(m => new MethodRow(m)).ToList();
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source))
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: true);
        foreach (var directory in Directory.EnumerateDirectories(source))
            CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)));
    }

    private sealed class MethodRow
    {
        public MethodRow(MethodRecord method) => Method = method;
        public MethodRecord Method { get; }
        public override string ToString() => Method.Status + "  " + Method.DeclaringType + "." + Method.Name;
    }
}
