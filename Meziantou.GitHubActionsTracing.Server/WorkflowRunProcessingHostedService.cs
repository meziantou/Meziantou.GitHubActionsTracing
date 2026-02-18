using Meziantou.Framework;
using Microsoft.Extensions.Options;

namespace Meziantou.GitHubActionsTracing.Server;

internal sealed class WorkflowRunProcessingHostedService(
    IWorkflowRunProcessingQueue queue,
    IOptionsMonitor<WebhookProcessingOptions> optionsAccessor,
    ILogger<WorkflowRunProcessingHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var maxDegreeOfParallelism = GetMaxDegreeOfParallelism(optionsAccessor.CurrentValue.MaxDegreeOfParallelism);
        logger.LogInformation("Workflow run processor started with max degree of parallelism {MaxDegreeOfParallelism}", maxDegreeOfParallelism);

        try
        {
            await Parallel.ForEachAsync(
                queue.DequeueAllAsync(stoppingToken),
                new ParallelOptions { MaxDegreeOfParallelism = maxDegreeOfParallelism, CancellationToken = stoppingToken },
                ProcessQueueItemAsync);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private async ValueTask ProcessQueueItemAsync(WorkflowRunProcessingItem item, CancellationToken cancellationToken)
    {
        try
        {
            await ProcessWorkflowRunAsync(item, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error processing workflow run {WorkflowRunUrl} (delivery: {DeliveryId})", item.WorkflowRunUrl, item.DeliveryId);
        }
    }

    private static int GetMaxDegreeOfParallelism(int maxDegreeOfParallelism)
    {
        return maxDegreeOfParallelism > 0 ? maxDegreeOfParallelism : 1;
    }

    private async Task ProcessWorkflowRunAsync(WorkflowRunProcessingItem item, CancellationToken cancellationToken)
    {
        var options = optionsAccessor.CurrentValue;
        var exportOptions = CreateExportOptions(item.WorkflowRunUrl, options);

        logger.LogInformation("Processing workflow run {WorkflowRunUrl} (delivery: {DeliveryId})", item.WorkflowRunUrl, item.DeliveryId);
        await WorkflowRunProcessor.ProcessAsync(exportOptions, cancellationToken);
        logger.LogInformation("Completed workflow run {WorkflowRunUrl} (delivery: {DeliveryId})", item.WorkflowRunUrl, item.DeliveryId);
    }

    private ExportOptions CreateExportOptions(Uri workflowRunUrl, WebhookProcessingOptions options)
    {
        var workflowRunId = GitHubRunIdentifier.TryParse(workflowRunUrl, out var runIdentifier) ? runIdentifier.RunId : (long?)null;
        var otelPath = ResolveOutputPath(options.OtelPath, nameof(options.OtelPath), workflowRunId, ".otel.json");
        var chromiumPath = ResolveOutputPath(options.ChromiumPath, nameof(options.ChromiumPath), workflowRunId, ".chromium.json");
        var speedscopePath = ResolveOutputPath(options.SpeedscopePath, nameof(options.SpeedscopePath), workflowRunId, ".speedscope.json");
        var htmlPath = ResolveOutputPath(options.HtmlPath, nameof(options.HtmlPath), workflowRunId, ".html");

        var format = GetExportFormat(options, otelPath);

        return new ExportOptions(
            WorkflowRunUrl: workflowRunUrl,
            WorkflowRunFolder: null,
            Format: format,
            OtelEndpoint: options.OtelEndpoint,
            OtelProtocol: options.GetOtlpProtocol(),
            OtelPath: otelPath,
            ChromiumPath: chromiumPath,
            SpeedscopePath: speedscopePath,
            HtmlPath: htmlPath,
            MinimumTestDuration: options.MinimumTestDuration,
            MinimumBinlogDuration: options.MinimumBinlogDuration,
            IncludeBinlog: options.IncludeBinlog,
            IncludeTests: options.IncludeTests);
    }

    private FullPath? ResolveOutputPath(string? configuredPath, string optionName, long? workflowRunId, string extension)
    {
        var parsedPath = TryParseFullPath(configuredPath, optionName);
        if (parsedPath is null)
        {
            return null;
        }

        var path = parsedPath.Value;
        if (IsFilePath(configuredPath!, path))
        {
            return path;
        }

        if (workflowRunId is null)
        {
            logger.LogWarning("Cannot infer workflow run id from URL. Value configured for {OptionName} is treated as a file path: {Path}", optionName, path);
            return path;
        }

        return path / string.Create(CultureInfo.InvariantCulture, $"{workflowRunId}{extension}");
    }

    private static bool IsFilePath(string configuredPath, FullPath path)
    {
        if (File.Exists(path))
        {
            return true;
        }

        if (Directory.Exists(path))
        {
            return false;
        }

        var lastCharacter = configuredPath[^1];
        if (lastCharacter == Path.DirectorySeparatorChar || lastCharacter == Path.AltDirectorySeparatorChar)
        {
            return false;
        }

        return !string.IsNullOrEmpty(Path.GetExtension(path));
    }

    private static ExportFormat? GetExportFormat(WebhookProcessingOptions options, FullPath? otelPath)
    {
        if (string.IsNullOrWhiteSpace(options.OtelEndpoint) && otelPath is null && !HasCollectorConfigurationFromExporterEnvironment())
        {
            return ExportFormat.OtelFile;
        }

        return null;
    }

    private static bool HasCollectorConfigurationFromExporterEnvironment()
    {
        foreach (System.Collections.DictionaryEntry environmentVariable in Environment.GetEnvironmentVariables())
        {
            if (environmentVariable.Key is not string name ||
                environmentVariable.Value is not string value ||
                string.IsNullOrWhiteSpace(value) ||
                !name.StartsWith("EXPORTER_OTEL_EXPORTER_OTLP", StringComparison.Ordinal))
            {
                continue;
            }

            return true;
        }

        return false;
    }

    private FullPath? TryParseFullPath(string? path, string optionName)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            return FullPath.FromPath(path);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Invalid path configured for {OptionName}: {Path}", optionName, path);
            return null;
        }
    }
}
