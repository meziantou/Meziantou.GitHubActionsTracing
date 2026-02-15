using System.Diagnostics;

namespace Meziantou.GitHubActionsTracing.Exporters;

internal sealed class ExportedActivity
{
    public string TraceId { get; init; } = string.Empty;

    public string SpanId { get; init; } = string.Empty;

    public string ParentSpanId { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;

    public ActivityKind Kind { get; init; }

    public DateTime StartTime { get; init; }

    public DateTime EndTime { get; init; }

    public Dictionary<string, object?> Attributes { get; init; } = new(StringComparer.Ordinal);

    public List<ExportedActivityEvent> Events { get; init; } = [];
}
