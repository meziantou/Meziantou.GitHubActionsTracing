namespace Meziantou.GitHubActionsTracing.Exporters;

internal sealed class ExportedActivityEvent
{
    public string Name { get; init; } = string.Empty;

    public DateTimeOffset Timestamp { get; init; }

    public Dictionary<string, object?> Attributes { get; init; } = new(StringComparer.Ordinal);
}
