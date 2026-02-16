using System.CommandLine;
using System.CommandLine.Parsing;
using Meziantou.Framework;
using Meziantou.GitHubActionsTracing.Exporters;
using OpenTelemetry.Exporter;

namespace Meziantou.GitHubActionsTracing;

internal static class CliApplication
{
    private const string OTelEndpointEnvironmentVariable = "OTEL_EXPORTER_OTLP_ENDPOINT";

    public static async Task<int> RunAsync(string[] args)
    {
        var workflowRunInputArgument = new Argument<WorkflowRunInput>("workflow-run-url-or-folder")
        {
            Description = "URL of the GitHub Actions workflow run or a downloaded run-info folder",
            CustomParser = ParseWorkflowRunInput,
        };

        var formatOption = new Option<ExportFormat?>("--format")
        {
            Description = "Output format: otel, otel-file, chromium, speedscope",
            CustomParser = ParseExportFormat,
        };

        var otelEndpointOption = new Option<string?>("--otel-endpoint")
        {
            Description = "OpenTelemetry collector endpoint",
        };

        var otelProtocolOption = new Option<OtlpExportProtocol>("--otel-protocol")
        {
            Description = "OpenTelemetry protocol: grpc, http, http/protobuf",
            DefaultValueFactory = static _ => OtlpExportProtocol.Grpc,
            CustomParser = ParseOtelProtocol,
        };

        var otelPathOption = new Option<FullPath?>("--otel-path", "--otel-file-path")
        {
            Description = "Export OpenTelemetry data to a file",
            CustomParser = ParseNullableFullPath,
        };

        var chromiumPathOption = new Option<FullPath?>("--chromium-path")
        {
            Description = "Export trace to Chromium format file",
            CustomParser = ParseNullableFullPath,
        };

        var speedscopePathOption = new Option<FullPath?>("--speedscope-path")
        {
            Description = "Export trace to Speedscope format file",
            CustomParser = ParseNullableFullPath,
        };

        var minimumTestDurationOption = new Option<TimeSpan>("--minimum-test-duration")
        {
            Description = "Exclude tests shorter than this duration (e.g. 00:00:01)",
            DefaultValueFactory = static _ => TimeSpan.Zero,
            CustomParser = ParseTimeSpan,
        };

        var minimumBinlogDurationOption = new Option<TimeSpan>("--minimum-binlog-duration", "--minimum-target-duration")
        {
            Description = "Exclude binlog targets shorter than this duration (e.g. 00:00:01)",
            DefaultValueFactory = static _ => TimeSpan.Zero,
            CustomParser = ParseTimeSpan,
        };

        var includeBinlogOption = new Option<bool>("--include-binlog")
        {
            Description = "Include MSBuild binlog targets/tasks in the trace",
            DefaultValueFactory = static _ => true,
        };

        var includeTestsOption = new Option<bool>("--include-tests")
        {
            Description = "Include TRX/JUnit tests in the trace",
            DefaultValueFactory = static _ => true,
        };

        var downloadRunInfoUrlArgument = new Argument<Uri>("url")
        {
            Description = "URL of the GitHub Actions workflow run",
            CustomParser = ParseWorkflowRunUri,
        };

        var downloadRunInfoOutputOption = new Option<FullPath>("--output")
        {
            Description = "Destination folder",
            Required = true,
            CustomParser = ParseFullPath,
        };

        var downloadRunInfoCommand = new Command("download-run-info", "Download GitHub Actions workflow run info and artifacts");
        downloadRunInfoCommand.Arguments.Add(downloadRunInfoUrlArgument);
        downloadRunInfoCommand.Options.Add(downloadRunInfoOutputOption);

        downloadRunInfoCommand.SetAction(async (parseResult, cancellationToken) =>
        {
            try
            {
                var url = parseResult.GetRequiredValue(downloadRunInfoUrlArgument);
                var outputDirectory = parseResult.GetRequiredValue(downloadRunInfoOutputOption);

                AppLog.Section("Downloading GitHub Actions run info");
                var downloadedPath = await GitHubRunDownloader.DownloadRunInfoAsync(url, outputDirectory, cancellationToken);
                AppLog.Info($"Output folder: {downloadedPath}");
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex.Message);
                return 1;
            }
        });

        var exportCommand = new Command("export", "Download and trace a GitHub Actions workflow run, or trace a downloaded run-info folder");
        exportCommand.Arguments.Add(workflowRunInputArgument);
        exportCommand.Options.Add(formatOption);
        exportCommand.Options.Add(otelEndpointOption);
        exportCommand.Options.Add(otelProtocolOption);
        exportCommand.Options.Add(otelPathOption);
        exportCommand.Options.Add(chromiumPathOption);
        exportCommand.Options.Add(speedscopePathOption);
        exportCommand.Options.Add(minimumTestDurationOption);
        exportCommand.Options.Add(minimumBinlogDurationOption);
        exportCommand.Options.Add(includeBinlogOption);
        exportCommand.Options.Add(includeTestsOption);

        exportCommand.SetAction(async (parseResult, cancellationToken) =>
        {
            try
            {
                var workflowRunInput = parseResult.GetRequiredValue(workflowRunInputArgument);
                var format = parseResult.GetValue(formatOption);
                var otelEndpoint = parseResult.GetValue(otelEndpointOption);
                var otelProtocol = parseResult.GetValue(otelProtocolOption);
                var otelPath = parseResult.GetValue(otelPathOption);
                var chromiumPath = parseResult.GetValue(chromiumPathOption);
                var speedscopePath = parseResult.GetValue(speedscopePathOption);
                var minimumTestDuration = parseResult.GetValue(minimumTestDurationOption);
                var minimumBinlogDuration = parseResult.GetValue(minimumBinlogDurationOption);
                var includeBinlog = parseResult.GetValue(includeBinlogOption);
                var includeTests = parseResult.GetValue(includeTestsOption);

                var effectiveOtelEndpoint = string.IsNullOrWhiteSpace(otelEndpoint)
                    ? Environment.GetEnvironmentVariable(OTelEndpointEnvironmentVariable)
                    : otelEndpoint;

                var options = new ApplicationOptions(
                    WorkflowRunUrl: workflowRunInput.WorkflowRunUrl,
                    WorkflowRunFolder: workflowRunInput.WorkflowRunFolder,
                    Format: format,
                    OtelEndpoint: effectiveOtelEndpoint,
                    OtelProtocol: otelProtocol,
                    OtelPath: otelPath,
                    ChromiumPath: chromiumPath,
                    SpeedscopePath: speedscopePath,
                    MinimumTestDuration: minimumTestDuration,
                    MinimumBinlogDuration: minimumBinlogDuration,
                    IncludeBinlog: includeBinlog,
                    IncludeTests: includeTests);

                await ExecuteAsync(options, cancellationToken);
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex.Message);
                return 1;
            }
        });

        var rootCommand = new RootCommand("GitHub Actions tracing tools")
        {
            exportCommand,
            downloadRunInfoCommand,
        };

        try
        {
            var parseResult = rootCommand.Parse(args);
            return await parseResult.InvokeAsync();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Fatal error: {ex.Message}");
            return 1;
        }
    }

    private static async Task ExecuteAsync(ApplicationOptions options, CancellationToken cancellationToken)
    {
        if (options.WorkflowRunFolder is not null)
        {
            AppLog.Section("Using local workflow run data");
            AppLog.Info($"Input folder: {options.WorkflowRunFolder}");
            await ExecuteFromDirectoryAsync(options.WorkflowRunFolder.Value, options);
            return;
        }

        if (options.WorkflowRunUrl is null)
        {
            throw new InvalidOperationException("Missing workflow run URL or folder path");
        }

        AppLog.Section("Downloading workflow run data");
        await using var temporaryDirectory = TemporaryDirectory.Create();

        var downloadedPath = await GitHubRunDownloader.DownloadAsync(options.WorkflowRunUrl, temporaryDirectory, cancellationToken);
        AppLog.Info($"Temporary folder: {downloadedPath}");

        await ExecuteFromDirectoryAsync(downloadedPath, options);
    }

    private static async Task ExecuteFromDirectoryAsync(FullPath path, ApplicationOptions options)
    {
        AppLog.Section("Creating trace model");
        var traceModel = TraceModel.Load(path, new TraceLoadOptions
        {
            IncludeBinlog = options.IncludeBinlog,
            IncludeTests = options.IncludeTests,
            MinimumBinlogDuration = options.MinimumBinlogDuration,
            MinimumTestDuration = options.MinimumTestDuration,
        });

        AppLog.Section("Exporting trace");
        var exportOptions = ExportOptions.Create(traceModel, options);
        var exporters = CreateTraceExporters(exportOptions);
        await TraceExporter.ExportAsync(traceModel, exporters);
    }

    private static List<ITraceExporter> CreateTraceExporters(ExportOptions options)
    {
        var exporters = new List<ITraceExporter>();

        if (options.ChromiumPath is not null)
        {
            exporters.Add(new ChromiumTraceExporter(options.ChromiumPath.Value));
        }

        if (options.SpeedscopePath is not null)
        {
            exporters.Add(new SpeedscopeTraceExporter(options.SpeedscopePath.Value));
        }

        if (!string.IsNullOrWhiteSpace(options.OtelEndpoint) || options.OtelPath is not null)
        {
            exporters.Add(new OpenTelemetryTraceExporter(options.OtelEndpoint, options.OtelProtocol, options.OtelPath));
        }

        return exporters;
    }

    private static Uri ParseWorkflowRunUri(ArgumentResult result)
    {
        if (result.Tokens.Count is 0)
            return SetError<Uri>(result, "Missing workflow run URL");

        if (Uri.TryCreate(result.Tokens[0].Value, UriKind.Absolute, out var uri))
            return uri;

        return SetError<Uri>(result, "Invalid workflow run URL");
    }

    private static WorkflowRunInput ParseWorkflowRunInput(ArgumentResult result)
    {
        if (result.Tokens.Count is 0)
            return SetError<WorkflowRunInput>(result, "Missing workflow run URL or folder path");

        var value = result.Tokens[0].Value;
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
            (string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
             string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)))
        {
            return WorkflowRunInput.FromWorkflowRunUrl(uri);
        }

        try
        {
            var folder = FullPath.FromPath(value);
            if (!Directory.Exists(folder))
            {
                return SetError<WorkflowRunInput>(result, $"Directory not found: {folder}");
            }

            return WorkflowRunInput.FromWorkflowRunFolder(folder);
        }
        catch (Exception ex)
        {
            return SetError<WorkflowRunInput>(result, ex.Message);
        }
    }

    private static ExportFormat? ParseExportFormat(ArgumentResult result)
    {
        if (result.Tokens.Count is 0)
            return null;

        var value = result.Tokens[0].Value;
        return value.ToUpperInvariant() switch
        {
            "OTEL" => ExportFormat.Otel,
            "OTEL-FILE" => ExportFormat.OtelFile,
            "CHROMIUM" => ExportFormat.Chromium,
            "SPEEDSCOPE" => ExportFormat.Speedscope,
            _ => SetError<ExportFormat?>(result, "Invalid format. Allowed values: otel, otel-file, chromium, speedscope"),
        };
    }

    private static OtlpExportProtocol ParseOtelProtocol(ArgumentResult result)
    {
        if (result.Tokens.Count is 0)
            return OtlpExportProtocol.Grpc;

        var value = result.Tokens[0].Value;
        return value.ToUpperInvariant() switch
        {
            "GRPC" => OtlpExportProtocol.Grpc,
            "HTTP" => OtlpExportProtocol.HttpProtobuf,
            "HTTP/PROTOBUF" => OtlpExportProtocol.HttpProtobuf,
            _ => SetError<OtlpExportProtocol>(result, "Invalid --otel-protocol. Allowed values: grpc, http, http/protobuf"),
        };
    }

    private static FullPath? ParseNullableFullPath(ArgumentResult result)
    {
        if (result.Tokens.Count is 0)
            return null;

        try
        {
            return FullPath.FromPath(result.Tokens[0].Value);
        }
        catch (Exception ex)
        {
            return SetError<FullPath?>(result, ex.Message);
        }
    }

    private static FullPath ParseFullPath(ArgumentResult result)
    {
        if (result.Tokens.Count is 0)
            return SetError<FullPath>(result, "Missing path");

        try
        {
            return FullPath.FromPath(result.Tokens[0].Value);
        }
        catch (Exception ex)
        {
            return SetError<FullPath>(result, ex.Message);
        }
    }

    private static TimeSpan ParseTimeSpan(ArgumentResult result)
    {
        if (result.Tokens.Count is 0)
            return TimeSpan.Zero;

        if (TimeSpan.TryParse(result.Tokens[0].Value, out var value))
            return value;

        return SetError<TimeSpan>(result, "Invalid TimeSpan value. Example: 00:00:01");
    }

    private static T SetError<T>(ArgumentResult result, string message)
    {
        result.AddError(message);
        return default!;
    }

    private sealed record WorkflowRunInput(Uri? WorkflowRunUrl, FullPath? WorkflowRunFolder)
    {
        public static WorkflowRunInput FromWorkflowRunUrl(Uri workflowRunUrl) => new(workflowRunUrl, null);

        public static WorkflowRunInput FromWorkflowRunFolder(FullPath workflowRunFolder) => new(null, workflowRunFolder);
    }
}
