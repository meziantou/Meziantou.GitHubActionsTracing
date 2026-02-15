namespace Meziantou.GitHubActionsTracing;

internal sealed class TraceSpan
{
    public string Id { get; init; } = null!;

    public string? ParentId { get; set; }

    public long? JobId { get; init; }

    public string Name { get; init; } = string.Empty;

    public string Kind { get; init; } = string.Empty;

    public DateTimeOffset StartTime { get; set; }

    public DateTimeOffset EndTime { get; set; }

    public TimeSpan Duration => EndTime >= StartTime ? EndTime - StartTime : TimeSpan.Zero;

    public Dictionary<string, object?> Attributes { get; init; } = new(StringComparer.Ordinal);

    public List<TraceEvent> Events { get; } = [];
}
