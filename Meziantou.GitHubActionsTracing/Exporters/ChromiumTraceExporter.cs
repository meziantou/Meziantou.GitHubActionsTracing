using Meziantou.Framework;
using Meziantou.Framework.ChromiumTracing;

namespace Meziantou.GitHubActionsTracing.Exporters;

internal sealed class ChromiumTraceExporter : ITraceExporter
{
    private readonly FullPath _outputPath;

    public ChromiumTraceExporter(FullPath outputPath)
    {
        _outputPath = outputPath;
    }

    public async Task ExportAsync(TraceModel model)
    {
        AppLog.Info($"Writing Chromium trace: {_outputPath}");
        await ExportToFileAsync(model, _outputPath);
    }

    private static async Task ExportToFileAsync(TraceModel model, FullPath outputPath)
    {
        outputPath.CreateParentDirectory();

        await using var writer = ChromiumTracingWriter.Create(outputPath);
        const int ProcessId = 1;

        await writer.WriteEventAsync(ChromiumTracingMetadataEvent.ProcessName(ProcessId, "GitHub Actions Workflow Run"));

        foreach (var span in model.Spans.OrderBy(static span => span.StartTime))
        {
            var threadId = span.JobId is null
                ? 1
                : Math.Abs((int)(span.JobId.Value % int.MaxValue)) + 2;

            await writer.WriteEventAsync(new ChromiumTracingCompleteEvent
            {
                Name = span.Name,
                Category = span.Kind,
                Timestamp = span.StartTime,
                Duration = span.Duration,
                ProcessId = ProcessId,
                ThreadId = threadId,
                Arguments = BuildArguments(span),
            });
        }
    }

    private static Dictionary<string, object?> BuildArguments(TraceSpan span)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["kind"] = span.Kind,
            ["events.count"] = NormalizeArgumentValue(span.Events.Count),
        };

        foreach (var attribute in span.Attributes)
        {
            result[attribute.Key] = NormalizeArgumentValue(attribute.Value);
        }

        return result;
    }

    private static string? NormalizeArgumentValue(object? value)
    {
        if (value is null)
            return null;

        if (value is string stringValue)
            return stringValue;

        if (value is IFormattable formattable)
            return formattable.ToString(null, CultureInfo.InvariantCulture);

        return value switch
        {
            _ => value.ToString(),
        };
    }
}
