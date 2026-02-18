namespace Meziantou.GitHubActionsTracing;

public sealed class TraceEvent
{
    public string Name { get; init; } = string.Empty;

    public string Message { get; init; } = string.Empty;

    public DateTimeOffset Timestamp { get; init; }

    public Dictionary<string, object?> Attributes { get; init; } = new(StringComparer.Ordinal);
}
