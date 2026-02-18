using System.Diagnostics;
using System.Globalization;
using Meziantou.Framework;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
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
    private const string OtelResourceAttributesEnvironmentVariableName = "OTEL_RESOURCE_ATTRIBUTES";
    private const string OtelExporterOtlpEndpointEnvironmentVariableName = "OTEL_EXPORTER_OTLP_ENDPOINT";
    private const string OtelExporterOtlpProtocolEnvironmentVariableName = "OTEL_EXPORTER_OTLP_PROTOCOL";
    private const string OtelExporterOtlpHeadersEnvironmentVariableName = "OTEL_EXPORTER_OTLP_HEADERS";
    private const string OtelExporterOtlpTimeoutEnvironmentVariableName = "OTEL_EXPORTER_OTLP_TIMEOUT";
    private const string OtelExporterOtlpTracesEndpointEnvironmentVariableName = "OTEL_EXPORTER_OTLP_TRACES_ENDPOINT";
    private const string OtelExporterOtlpTracesProtocolEnvironmentVariableName = "OTEL_EXPORTER_OTLP_TRACES_PROTOCOL";
    private const string OtelExporterOtlpTracesHeadersEnvironmentVariableName = "OTEL_EXPORTER_OTLP_TRACES_HEADERS";
    private const string OtelExporterOtlpTracesTimeoutEnvironmentVariableName = "OTEL_EXPORTER_OTLP_TRACES_TIMEOUT";
    private const string OtelBspScheduleDelayEnvironmentVariableName = "OTEL_BSP_SCHEDULE_DELAY";
    private const string OtelBspMaxQueueSizeEnvironmentVariableName = "OTEL_BSP_MAX_QUEUE_SIZE";
    private const string OtelBspMaxExportBatchSizeEnvironmentVariableName = "OTEL_BSP_MAX_EXPORT_BATCH_SIZE";
    private const string OtelBspExportTimeoutEnvironmentVariableName = "OTEL_BSP_EXPORT_TIMEOUT";

    private const string ExporterEnvironmentVariablePrefix = "EXPORTER_";
    private const string ExporterOtelEnvironmentVariablePrefix = "EXPORTER_OTEL_";
    private const string ExporterOtelExporterEnvironmentVariablePrefix = "EXPORTER_OTEL_EXPORTER_OTLP";

    private const string DefaultOtlpGrpcEndpoint = "http://localhost:4317";
    private const string DefaultOtlpHttpEndpoint = "http://localhost:4318";
    private const int DefaultTimeoutMilliseconds = 10000;
    private const int DefaultBspMaxQueueSize = 2048;
    private const int DefaultBspScheduleDelayMilliseconds = 5000;
    private const int DefaultBspExportTimeoutMilliseconds = 30000;
    private const int DefaultBspMaxExportBatchSize = 512;

    private readonly string? _otelEndpoint;
    private readonly FullPath? _otelPath;
    private readonly OtlpExportProtocol _otelProtocol;

    public OpenTelemetryTraceExporter(string? otelEndpoint, OtlpExportProtocol otelProtocol, FullPath? otelPath)
    {
        if (string.IsNullOrWhiteSpace(otelEndpoint) && otelPath is null && !HasCollectorConfigurationFromExporterEnvironment())
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

        var exporterOtelConfiguration = CreateExporterOtelConfiguration();

        var serviceName = exporterOtelConfiguration[OtelServiceNameEnvironmentVariableName];
        if (string.IsNullOrWhiteSpace(serviceName))
        {
            serviceName = DefaultServiceName;
        }

        var resourceBuilder = ResourceBuilder
            .CreateEmpty();

        AddResourceAttributesFromConfiguration(resourceBuilder, exporterOtelConfiguration);
        resourceBuilder.AddService(serviceName: serviceName);

        using var activitySource = new ActivitySource(ActivitySourceName);
        var tracerProviderBuilder = Sdk
            .CreateTracerProviderBuilder()
            .SetResourceBuilder(resourceBuilder)
            .SetSampler(new AlwaysOnSampler())
            .AddSource(ActivitySourceName);
        tracerProviderBuilder.ConfigureServices(services => services.AddSingleton<IConfiguration>(exporterOtelConfiguration));

        if (!string.IsNullOrWhiteSpace(_otelEndpoint) || HasCollectorConfigurationFromExporterEnvironment())
        {
            tracerProviderBuilder.AddOtlpExporter();
            AppLog.Info($"OpenTelemetry collector endpoint: {exporterOtelConfiguration[OtelExporterOtlpTracesEndpointEnvironmentVariableName]} ({exporterOtelConfiguration[OtelExporterOtlpTracesProtocolEnvironmentVariableName]})");
        }

        OtelJsonFileExporter? otelFileExporter = null;
        SimpleActivityExportProcessor? otelFileProcessor = null;

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

    private static bool HasCollectorConfigurationFromExporterEnvironment()
    {
        foreach (System.Collections.DictionaryEntry environmentVariable in Environment.GetEnvironmentVariables())
        {
            if (environmentVariable.Key is not string name ||
                environmentVariable.Value is not string value ||
                string.IsNullOrWhiteSpace(value) ||
                !name.StartsWith(ExporterOtelExporterEnvironmentVariablePrefix, StringComparison.Ordinal))
            {
                continue;
            }

            return true;
        }

        return false;
    }

    private static void AddResourceAttributesFromConfiguration(ResourceBuilder resourceBuilder, IConfiguration configuration)
    {
        var rawAttributes = configuration[OtelResourceAttributesEnvironmentVariableName];
        if (string.IsNullOrWhiteSpace(rawAttributes))
            return;

        var attributes = new Dictionary<string, object>(StringComparer.Ordinal);

        foreach (var attribute in rawAttributes.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separatorIndex = attribute.IndexOf('=', StringComparison.Ordinal);
            if (separatorIndex <= 0)
                continue;

            var key = attribute[..separatorIndex].Trim();
            if (string.IsNullOrEmpty(key))
                continue;

            var value = attribute[(separatorIndex + 1)..].Trim();
            attributes[key] = value;
        }

        if (attributes.Count > 0)
        {
            resourceBuilder.AddAttributes(attributes);
        }
    }

    private IConfigurationRoot CreateExporterOtelConfiguration()
    {
        var values = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [OtelExporterOtlpProtocolEnvironmentVariableName] = ToOtelProtocolValue(_otelProtocol),
            [OtelExporterOtlpEndpointEnvironmentVariableName] = _otelProtocol is OtlpExportProtocol.HttpProtobuf ? DefaultOtlpHttpEndpoint : DefaultOtlpGrpcEndpoint,
            [OtelExporterOtlpTimeoutEnvironmentVariableName] = DefaultTimeoutMilliseconds.ToString(CultureInfo.InvariantCulture),
            [OtelBspMaxQueueSizeEnvironmentVariableName] = DefaultBspMaxQueueSize.ToString(CultureInfo.InvariantCulture),
            [OtelBspScheduleDelayEnvironmentVariableName] = DefaultBspScheduleDelayMilliseconds.ToString(CultureInfo.InvariantCulture),
            [OtelBspExportTimeoutEnvironmentVariableName] = DefaultBspExportTimeoutMilliseconds.ToString(CultureInfo.InvariantCulture),
            [OtelBspMaxExportBatchSizeEnvironmentVariableName] = DefaultBspMaxExportBatchSize.ToString(CultureInfo.InvariantCulture),
        };

        foreach (System.Collections.DictionaryEntry environmentVariable in Environment.GetEnvironmentVariables())
        {
            if (environmentVariable.Key is not string name ||
                !name.StartsWith(ExporterOtelEnvironmentVariablePrefix, StringComparison.Ordinal))
            {
                continue;
            }

            var mappedName = name[ExporterEnvironmentVariablePrefix.Length..];
            values[mappedName] = environmentVariable.Value?.ToString();
        }

        if (!string.IsNullOrWhiteSpace(_otelEndpoint))
        {
            values[OtelExporterOtlpEndpointEnvironmentVariableName] = _otelEndpoint;
            values[OtelExporterOtlpProtocolEnvironmentVariableName] = ToOtelProtocolValue(_otelProtocol);
        }

        if (!values.TryGetValue(OtelExporterOtlpTracesProtocolEnvironmentVariableName, out _))
        {
            values[OtelExporterOtlpTracesProtocolEnvironmentVariableName] = values[OtelExporterOtlpProtocolEnvironmentVariableName];
        }

        if (!values.TryGetValue(OtelExporterOtlpTracesHeadersEnvironmentVariableName, out _) && values.TryGetValue(OtelExporterOtlpHeadersEnvironmentVariableName, out var headers))
        {
            values[OtelExporterOtlpTracesHeadersEnvironmentVariableName] = headers;
        }

        if (!values.TryGetValue(OtelExporterOtlpTracesTimeoutEnvironmentVariableName, out _) && values.TryGetValue(OtelExporterOtlpTimeoutEnvironmentVariableName, out var timeout))
        {
            values[OtelExporterOtlpTracesTimeoutEnvironmentVariableName] = timeout;
        }

        if (!values.TryGetValue(OtelExporterOtlpTracesEndpointEnvironmentVariableName, out _))
        {
            values.TryGetValue(OtelExporterOtlpEndpointEnvironmentVariableName, out var endpoint);
            values.TryGetValue(OtelExporterOtlpTracesProtocolEnvironmentVariableName, out var protocol);
            values[OtelExporterOtlpTracesEndpointEnvironmentVariableName] = CreateTracesEndpoint(
                endpoint,
                protocol);
        }

        return new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();
    }

    private static string ToOtelProtocolValue(OtlpExportProtocol protocol)
    {
        return protocol switch
        {
            OtlpExportProtocol.HttpProtobuf => "http/protobuf",
            _ => "grpc",
        };
    }

    private static string CreateTracesEndpoint(string? baseEndpoint, string? protocol)
    {
        if (string.IsNullOrWhiteSpace(baseEndpoint))
        {
            return string.Equals(protocol, "http/protobuf", StringComparison.OrdinalIgnoreCase)
                ? "http://localhost:4318/v1/traces"
                : "http://localhost:4317";
        }

        if (!string.Equals(protocol, "http/protobuf", StringComparison.OrdinalIgnoreCase))
            return baseEndpoint;

        var uri = new Uri(baseEndpoint, UriKind.Absolute);
        if (uri.AbsolutePath.EndsWith("/v1/traces", StringComparison.OrdinalIgnoreCase))
            return uri.AbsoluteUri;

        var builder = new UriBuilder(uri)
        {
            Path = uri.AbsolutePath.TrimEnd('/') + "/v1/traces",
        };
        return builder.Uri.AbsoluteUri;
    }
}
