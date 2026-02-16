using System.Diagnostics;
using Meziantou.Framework;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Meziantou.GitHubActionsTracing.Exporters;

internal sealed class OpenTelemetryTraceExporter : ITraceExporter
{
    private const string ActivitySourceName = "Meziantou.GitHubActionsTracing";
    private const string DefaultServiceName = "GitHub Actions";
    private const string OtelServiceNameEnvironmentVariableName = "OTEL_SERVICE_NAME";

    private readonly string? _otelEndpoint;
    private readonly FullPath? _otelPath;
    private readonly OtlpExportProtocol _otelProtocol;

    public OpenTelemetryTraceExporter(string? otelEndpoint, OtlpExportProtocol otelProtocol, FullPath? otelPath)
    {
        if (string.IsNullOrWhiteSpace(otelEndpoint) && otelPath is null)
        {
            throw new ArgumentException("At least one OpenTelemetry destination must be configured", nameof(otelEndpoint));
        }

        _otelEndpoint = string.IsNullOrWhiteSpace(otelEndpoint) ? null : otelEndpoint;
        _otelProtocol = otelProtocol;
        _otelPath = otelPath;
    }

    public Task ExportAsync(TraceModel model)
    {
        AppLog.Info("Exporting OpenTelemetry trace");

        var serviceName = System.Environment.GetEnvironmentVariable(OtelServiceNameEnvironmentVariableName);
        if (string.IsNullOrWhiteSpace(serviceName))
        {
            serviceName = DefaultServiceName;
        }

        var resourceBuilder = ResourceBuilder
            .CreateDefault()
            .AddService(serviceName: serviceName);

        using var activitySource = new ActivitySource(ActivitySourceName);
        var tracerProviderBuilder = Sdk
            .CreateTracerProviderBuilder()
            .SetResourceBuilder(resourceBuilder)
            .SetSampler(new AlwaysOnSampler())
            .AddSource(ActivitySourceName);
        OtelJsonFileExporter? otelFileExporter = null;
        SimpleActivityExportProcessor? otelFileProcessor = null;

        if (!string.IsNullOrWhiteSpace(_otelEndpoint))
        {
            tracerProviderBuilder.AddOtlpExporter(options =>
            {
                options.Endpoint = new Uri(_otelEndpoint, UriKind.Absolute);
                options.Protocol = _otelProtocol;
            });

            AppLog.Info($"OpenTelemetry collector endpoint: {_otelEndpoint} ({_otelProtocol})");
        }

        if (_otelPath is not null)
        {
            otelFileExporter = new OtelJsonFileExporter(_otelPath.Value);
            otelFileProcessor = new SimpleActivityExportProcessor(otelFileExporter);
            tracerProviderBuilder.AddProcessor(otelFileProcessor);
            AppLog.Info($"OpenTelemetry file: {_otelPath}");
        }

        var activityContexts = new Dictionary<string, ActivityContext>(StringComparer.Ordinal);
        string? rootSpanId = null;
        using (var tracerProvider = tracerProviderBuilder.Build())
        {
            foreach (var span in OrderSpans(model.Spans))
            {
                var parentContext = default(ActivityContext);
                if (span.ParentId is not null && activityContexts.TryGetValue(span.ParentId, out var resolvedParentContext))
                {
                    parentContext = resolvedParentContext;
                }

                using var activity = activitySource.StartActivity(span.Name, ActivityKind.Internal, parentContext, startTime: span.StartTime);
                if (activity is null)
                    continue;

                activity.SetTag("span.kind", span.Kind);
                if (span.JobId is not null)
                    activity.SetTag("github.job_id", span.JobId);

                foreach (var attribute in span.Attributes)
                {
                    activity.SetTag(attribute.Key, attribute.Value?.ToString());
                }

                foreach (var traceEvent in span.Events)
                {
                    var eventTags = new ActivityTagsCollection
                    {
                        { "message", traceEvent.Message },
                    };

                    foreach (var eventAttribute in traceEvent.Attributes)
                    {
                        eventTags.Add(eventAttribute.Key, eventAttribute.Value?.ToString());
                    }

                    activity.AddEvent(new ActivityEvent(traceEvent.Name, traceEvent.Timestamp, eventTags));
                }

                activity.SetEndTime(span.EndTime.UtcDateTime);
                activity.Stop();

                activityContexts[span.Id] = activity.Context;
                if (span.ParentId is null)
                    rootSpanId = activity.SpanId.ToHexString();
            }

            tracerProvider.ForceFlush();
        }

        otelFileProcessor?.Dispose();
        otelFileExporter?.Dispose();

        if (rootSpanId is not null)
        {
            AppLog.Info($"OpenTelemetry root span id: {rootSpanId}");
        }

        return Task.CompletedTask;
    }

    private static IEnumerable<TraceSpan> OrderSpans(IEnumerable<TraceSpan> spans)
    {
        var byId = spans.ToDictionary(static span => span.Id, StringComparer.Ordinal);

        return spans
            .OrderBy(span => ComputeDepth(span, byId))
            .ThenBy(static span => span.StartTime)
            .ThenBy(static span => span.EndTime);
    }

    private static int ComputeDepth(TraceSpan span, Dictionary<string, TraceSpan> byId)
    {
        var depth = 0;
        var currentSpan = span;

        while (currentSpan.ParentId is not null && byId.TryGetValue(currentSpan.ParentId, out var parent))
        {
            depth++;
            currentSpan = parent;
            if (depth > 1024)
                break;
        }

        return depth;
    }
}
