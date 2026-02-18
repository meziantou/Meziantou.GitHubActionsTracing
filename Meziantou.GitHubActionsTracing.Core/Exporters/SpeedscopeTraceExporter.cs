using System.Text.Json;
using Meziantou.Framework;

namespace Meziantou.GitHubActionsTracing.Exporters;

internal sealed class SpeedscopeTraceExporter : ITraceExporter
{
    private readonly FullPath _outputPath;

    private static readonly JsonSerializerOptions JsonSerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    public SpeedscopeTraceExporter(FullPath outputPath)
    {
        _outputPath = outputPath;
    }

    public async Task ExportAsync(TraceModel model)
    {
        AppLog.Info($"Writing Speedscope trace: {_outputPath}");
        await ExportToFileAsync(model, _outputPath);
    }

    private static async Task ExportToFileAsync(TraceModel model, FullPath outputPath)
    {
        outputPath.CreateParentDirectory();

        var frames = new List<Dictionary<string, string>>();
        var frameIndex = new Dictionary<string, int>(StringComparer.Ordinal);

        var profiles = model.Spans
            .Where(static span => span.Kind is "job")
            .Select(jobSpan => CreateProfile(model, jobSpan, frames, frameIndex))
            .ToList();

        var document = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["$schema"] = "https://www.speedscope.app/file-format-schema.json",
            ["name"] = $"GitHub run {model.WorkflowRunId}",
            ["shared"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["frames"] = frames,
            },
            ["profiles"] = profiles,
        };

        await using var stream = File.Create(outputPath);
        await JsonSerializer.SerializeAsync(stream, document, JsonSerializerOptions);
    }

    private static Dictionary<string, object?> CreateProfile(TraceModel model, TraceSpan jobSpan, List<Dictionary<string, string>> frames, Dictionary<string, int> frameIndex)
    {
        var profileSpans = model.Spans
            .Where(span => span.JobId == jobSpan.JobId)
            .OrderBy(static span => span.StartTime)
            .ToList();

        var events = new List<Dictionary<string, object?>>();

        foreach (var span in profileSpans)
        {
            var key = span.Kind + ":" + span.Name;
            if (!frameIndex.TryGetValue(key, out var index))
            {
                index = frames.Count;
                frameIndex[key] = index;
                frames.Add(new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["name"] = span.Name,
                });
            }

            var start = (span.StartTime - jobSpan.StartTime).TotalMilliseconds;
            var end = (span.EndTime - jobSpan.StartTime).TotalMilliseconds;

            events.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["type"] = "O",
                ["at"] = start,
                ["frame"] = index,
            });

            events.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["type"] = "C",
                ["at"] = end,
                ["frame"] = index,
            });
        }

        events = events
            .OrderBy(static evt => Convert.ToDouble(evt["at"], System.Globalization.CultureInfo.InvariantCulture))
            .ThenBy(evt => string.Equals((string?)evt["type"], "C", StringComparison.Ordinal) ? 0 : 1)
            .ToList();

        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["type"] = "evented",
            ["name"] = jobSpan.Name,
            ["unit"] = "milliseconds",
            ["startValue"] = 0,
            ["endValue"] = Math.Max(1, (jobSpan.EndTime - jobSpan.StartTime).TotalMilliseconds),
            ["events"] = events,
        };
    }
}
