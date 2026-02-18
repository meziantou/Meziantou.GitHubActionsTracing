using Meziantou.Framework;
using OpenTelemetry.Exporter;

namespace Meziantou.GitHubActionsTracing;

public sealed record ExportOptions(
    Uri? WorkflowRunUrl,
    FullPath? WorkflowRunFolder,
    ExportFormat? Format,
    string? OtelEndpoint,
    OtlpExportProtocol OtelProtocol,
    FullPath? OtelPath,
    FullPath? ChromiumPath,
    FullPath? SpeedscopePath,
    FullPath? HtmlPath,
    TimeSpan MinimumTestDuration,
    TimeSpan MinimumBinlogDuration,
    bool IncludeBinlog,
    bool IncludeTests);