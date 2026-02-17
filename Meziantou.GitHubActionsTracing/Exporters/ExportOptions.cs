using Meziantou.Framework;
using OpenTelemetry.Exporter;

namespace Meziantou.GitHubActionsTracing.Exporters;

internal sealed record ExportOptions(
    string? OtelEndpoint,
    OtlpExportProtocol OtelProtocol,
    FullPath? OtelPath,
    FullPath? ChromiumPath,
    FullPath? SpeedscopePath,
    FullPath? HtmlPath)
{
    public static ExportOptions Create(TraceModel model, ApplicationOptions options)
    {
        var otelEndpoint = options.OtelEndpoint;
        var otelPath = options.OtelPath;
        var chromiumPath = options.ChromiumPath;
        var speedscopePath = options.SpeedscopePath;
        var htmlPath = options.HtmlPath;
        var hasCollectorConfigurationFromExporterEnvironment = HasCollectorConfigurationFromExporterEnvironment();

        if (options.Format is not null)
        {
            switch (options.Format.Value)
            {
                case ExportFormat.Otel:
                    if (string.IsNullOrWhiteSpace(otelEndpoint) && !hasCollectorConfigurationFromExporterEnvironment)
                    {
                        throw new InvalidOperationException("--format otel requires --otel-endpoint or EXPORTER_OTEL_EXPORTER_OTLP_* environment variables");
                    }

                    break;
                case ExportFormat.OtelFile:
                    otelPath ??= FullPath.FromPath($"trace-{model.WorkflowRunId}.otel.json");
                    break;
                case ExportFormat.Chromium:
                    chromiumPath ??= FullPath.FromPath(string.Create(CultureInfo.InvariantCulture, $"trace-{model.WorkflowRunId}.chromium.json"));
                    break;
                case ExportFormat.Speedscope:
                    speedscopePath ??= FullPath.FromPath($"trace-{model.WorkflowRunId}.speedscope.json");
                    break;
                case ExportFormat.Html:
                    htmlPath ??= FullPath.FromPath($"trace-{model.WorkflowRunId}.html");
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(options), options.Format, "Unknown format");
            }
        }

        if (otelPath is null && chromiumPath is null && speedscopePath is null && htmlPath is null && string.IsNullOrWhiteSpace(otelEndpoint) && !hasCollectorConfigurationFromExporterEnvironment)
        {
            throw new InvalidOperationException("No exporter selected. Use --format, --otel-endpoint, --otel-path, --chromium-path, --speedscope-path, or --html-path");
        }

        return new ExportOptions(
            OtelEndpoint: string.IsNullOrWhiteSpace(otelEndpoint) ? null : otelEndpoint,
            OtelProtocol: options.OtelProtocol,
            OtelPath: otelPath,
            ChromiumPath: chromiumPath,
            SpeedscopePath: speedscopePath,
            HtmlPath: htmlPath);
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
}
