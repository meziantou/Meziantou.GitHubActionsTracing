using System.IO.Compression;
using Meziantou.Framework;
using Meziantou.GitHubActionsTracing.Exporters;

namespace Meziantou.GitHubActionsTracing;

internal static class WorkflowRunProcessor
{
    public static async Task ProcessAsync(ApplicationOptions options, CancellationToken cancellationToken)
    {
        if (options.WorkflowRunFolder is not null)
        {
            var folder = options.WorkflowRunFolder.Value;

            if (File.Exists(folder) && string.Equals(Path.GetExtension(folder), ".zip", StringComparison.OrdinalIgnoreCase))
            {
                AppLog.Section("Extracting zip file");
                AppLog.Info($"Input file: {folder}");
                await using var tempDir = TemporaryDirectory.Create();
                var extractedPath = (FullPath)tempDir;

                using (var archive = ZipFile.OpenRead(folder))
                {
                    archive.ExtractToDirectory(extractedPath, overwriteFiles: true);
                }

                AppLog.Info($"Temporary folder: {extractedPath}");
                await ProcessFromDirectoryAsync(extractedPath, options);
                return;
            }

            AppLog.Section("Using local workflow run data");
            AppLog.Info($"Input folder: {folder}");
            await ProcessFromDirectoryAsync(folder, options);
            return;
        }

        if (options.WorkflowRunUrl is null)
        {
            throw new InvalidOperationException("Missing workflow run URL or folder path");
        }

        AppLog.Section("Downloading workflow run data");
        await using var temporaryDirectory = TemporaryDirectory.Create();

        var downloadedPath = await GitHubRunDownloader.DownloadAsync(options.WorkflowRunUrl, temporaryDirectory, cancellationToken);
        AppLog.Info($"Temporary folder: {downloadedPath}");

        await ProcessFromDirectoryAsync(downloadedPath, options);
    }

    private static async Task ProcessFromDirectoryAsync(FullPath path, ApplicationOptions options)
    {
        AppLog.Section("Creating trace model");
        var traceModel = TraceModel.Load(path, new TraceLoadOptions
        {
            IncludeBinlog = options.IncludeBinlog,
            IncludeTests = options.IncludeTests,
            MinimumBinlogDuration = options.MinimumBinlogDuration,
            MinimumTestDuration = options.MinimumTestDuration,
        });

        AppLog.Section("Exporting trace");
        var exportOptions = ExportOptions.Create(traceModel, options);
        var exporters = CreateTraceExporters(exportOptions);
        await TraceExporter.ExportAsync(traceModel, exporters);
    }

    private static List<ITraceExporter> CreateTraceExporters(ExportOptions options)
    {
        var exporters = new List<ITraceExporter>();

        if (options.ChromiumPath is not null)
        {
            exporters.Add(new ChromiumTraceExporter(options.ChromiumPath.Value));
        }

        if (options.SpeedscopePath is not null)
        {
            exporters.Add(new SpeedscopeTraceExporter(options.SpeedscopePath.Value));
        }

        if (options.HtmlPath is not null)
        {
            exporters.Add(new HtmlTraceExporter(options.HtmlPath.Value));
        }

        if (!string.IsNullOrWhiteSpace(options.OtelEndpoint) || options.OtelPath is not null)
        {
            exporters.Add(new OpenTelemetryTraceExporter(options.OtelEndpoint, options.OtelProtocol, options.OtelPath));
        }

        return exporters;
    }
}
