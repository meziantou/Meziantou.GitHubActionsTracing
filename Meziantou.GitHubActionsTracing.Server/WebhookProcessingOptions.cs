using OpenTelemetry.Exporter;

namespace Meziantou.GitHubActionsTracing.Server;

internal sealed class WebhookProcessingOptions
{
    private GitHubRepositoryFilter? _repositoryFilter;

    public const string SectionName = "GitHubActionsTracingWebhook";

    public string? WebhookSecret { get; set; }

    public string? OtelEndpoint { get; set; }

    public string? OtelProtocol { get; set; }

    public string? OtelPath { get; set; }

    public string? ChromiumPath { get; set; }

    public string? SpeedscopePath { get; set; }

    public string? HtmlPath { get; set; }

    public bool IncludeBinlog { get; set; } = true;

    public bool IncludeTests { get; set; } = true;

    public int MaxDegreeOfParallelism { get; set; } = 1;

    public TimeSpan MinimumTestDuration { get; set; }

    public TimeSpan MinimumBinlogDuration { get; set; }

    public string[] AllowedRepositoriesExact { get; set; } = [];

    public string[] AllowedRepositoriesPatterns { get; set; } = [];

    public GitHubRepositoryFilter GetRepositoryFilter()
    {
        return _repositoryFilter ??= new GitHubRepositoryFilter(AllowedRepositoriesExact, AllowedRepositoriesPatterns);
    }

    public OtlpExportProtocol GetOtlpProtocol()
    {
        return OtelProtocol?.ToUpperInvariant() switch
        {
            "GRPC" => OtlpExportProtocol.Grpc,
            "HTTP" => OtlpExportProtocol.HttpProtobuf,
            "HTTP/PROTOBUF" => OtlpExportProtocol.HttpProtobuf,
            _ => OtlpExportProtocol.Grpc,
        };
    }
}
