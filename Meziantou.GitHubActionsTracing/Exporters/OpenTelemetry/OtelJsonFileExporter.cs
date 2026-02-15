using System.Diagnostics;
using System.Text.Json;
using Meziantou.Framework;
using OpenTelemetry;

namespace Meziantou.GitHubActionsTracing.Exporters;

internal sealed class OtelJsonFileExporter : BaseExporter<Activity>
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly Lock _gate = new();
    private readonly List<ExportedActivity> _exportedActivities = [];
    private readonly FullPath _outputPath;

    public OtelJsonFileExporter(FullPath outputPath)
    {
        _outputPath = outputPath;
    }

    public override ExportResult Export(in Batch<Activity> batch)
    {
        lock (_gate)
        {
            foreach (var activity in batch)
            {
                var attributes = new Dictionary<string, object?>(StringComparer.Ordinal);
                foreach (var tag in activity.TagObjects)
                {
                    attributes[tag.Key] = tag.Value;
                }

                var events = activity.Events
                    .Select(activityEvent => new ExportedActivityEvent
                    {
                        Name = activityEvent.Name,
                        Timestamp = activityEvent.Timestamp,
                        Attributes = activityEvent.Tags.ToDictionary(static item => item.Key, static item => (object?)item.Value, StringComparer.Ordinal),
                    })
                    .ToList();

                _exportedActivities.Add(new ExportedActivity
                {
                    TraceId = activity.TraceId.ToHexString(),
                    SpanId = activity.SpanId.ToHexString(),
                    ParentSpanId = activity.ParentSpanId.ToHexString(),
                    Name = activity.DisplayName,
                    Kind = activity.Kind,
                    StartTime = activity.StartTimeUtc,
                    EndTime = activity.StartTimeUtc + activity.Duration,
                    Attributes = attributes,
                    Events = events,
                });
            }
        }

        return ExportResult.Success;
    }

    protected override bool OnShutdown(int timeoutMilliseconds)
    {
        List<ExportedActivity> snapshot;
        lock (_gate)
        {
            snapshot = _exportedActivities.ToList();
        }

        var spans = snapshot
            .Select(activity => new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["traceId"] = activity.TraceId,
                ["spanId"] = activity.SpanId,
                ["parentSpanId"] = string.IsNullOrEmpty(activity.ParentSpanId) || activity.ParentSpanId is "0000000000000000" ? null : activity.ParentSpanId,
                ["name"] = activity.Name,
                ["kind"] = ToOtelSpanKind(activity.Kind),
                ["startTimeUnixNano"] = ToUnixTimeNano(activity.StartTime),
                ["endTimeUnixNano"] = ToUnixTimeNano(activity.EndTime),
                ["attributes"] = activity.Attributes.Select(static attribute => new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["key"] = attribute.Key,
                    ["value"] = ToAnyValue(attribute.Value),
                }).ToList(),
                ["events"] = activity.Events.Select(static activityEvent => new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["name"] = activityEvent.Name,
                    ["timeUnixNano"] = ToUnixTimeNano(activityEvent.Timestamp.UtcDateTime),
                    ["attributes"] = activityEvent.Attributes.Select(static attribute => new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["key"] = attribute.Key,
                        ["value"] = ToAnyValue(attribute.Value),
                    }).ToList(),
                }).ToList(),
            })
            .ToList();

        var document = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["resourceSpans"] = new[]
            {
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["resource"] = new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["attributes"] = new[]
                        {
                            new Dictionary<string, object?>(StringComparer.Ordinal)
                            {
                                ["key"] = "service.name",
                                ["value"] = ToAnyValue("Meziantou.GitHubActionsTracing"),
                            },
                        },
                    },
                    ["scopeSpans"] = new[]
                    {
                        new Dictionary<string, object?>(StringComparer.Ordinal)
                        {
                            ["scope"] = new Dictionary<string, object?>(StringComparer.Ordinal)
                            {
                                ["name"] = "Meziantou.GitHubActionsTracing",
                            },
                            ["spans"] = spans,
                        },
                    },
                },
            },
        };

        _outputPath.CreateParentDirectory();
        using var stream = File.Create(_outputPath);
        JsonSerializer.Serialize(stream, document, JsonOptions);

        return true;
    }

    private static Dictionary<string, object?> ToAnyValue(object? value)
    {
        return value switch
        {
            null => new Dictionary<string, object?>(StringComparer.Ordinal) { ["stringValue"] = null },
            bool booleanValue => new Dictionary<string, object?>(StringComparer.Ordinal) { ["boolValue"] = booleanValue },
            int intValue => new Dictionary<string, object?>(StringComparer.Ordinal) { ["intValue"] = intValue },
            long longValue => new Dictionary<string, object?>(StringComparer.Ordinal) { ["intValue"] = longValue },
            float floatValue => new Dictionary<string, object?>(StringComparer.Ordinal) { ["doubleValue"] = floatValue },
            double doubleValue => new Dictionary<string, object?>(StringComparer.Ordinal) { ["doubleValue"] = doubleValue },
            decimal decimalValue => new Dictionary<string, object?>(StringComparer.Ordinal) { ["doubleValue"] = (double)decimalValue },
            _ => new Dictionary<string, object?>(StringComparer.Ordinal) { ["stringValue"] = value.ToString() },
        };
    }

    private static long ToUnixTimeNano(DateTime dateTimeUtc)
    {
        var utc = dateTimeUtc.Kind is DateTimeKind.Utc
            ? dateTimeUtc
            : dateTimeUtc.ToUniversalTime();

        return (utc - DateTime.UnixEpoch).Ticks * 100;
    }

    private static int ToOtelSpanKind(ActivityKind activityKind)
    {
        return activityKind switch
        {
            ActivityKind.Internal => 1,
            ActivityKind.Server => 2,
            ActivityKind.Client => 3,
            ActivityKind.Producer => 4,
            ActivityKind.Consumer => 5,
            _ => 1,
        };
    }
}
