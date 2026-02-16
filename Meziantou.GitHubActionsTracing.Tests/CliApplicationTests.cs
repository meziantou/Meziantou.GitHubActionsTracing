using System.IO.Compression;
using System.Text.Json;
using Meziantou.Framework;
using Meziantou.Framework.InlineSnapshotTesting;
using Meziantou.GitHubActionsTracing.Exporters;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace Meziantou.GitHubActionsTracing.Tests;

public sealed class CliApplicationTests
{
    private static readonly SemaphoreSlim ConsoleSemaphore = new(1, 1);
    private const string EmbeddedFixtureFileName = "Run22048671188_Job63702111001.zip";

    [Fact]
    public async Task Root_Help_MatchesSnapshot()
    {
        var root = await InvokeCliAsync("--help");
        var actual = $$"""
            exit={{root.ExitCode}}
            {{root.Output}}
            """;

        InlineSnapshot.Validate(actual, """
            exit=0
            Description:
              GitHub Actions tracing tools

            Usage:
              Meziantou.GitHubActionsTracing.Tests [command] [options]

            Options:
              -?, -h, --help  Show help and usage information
              --version       Show version information

            Commands:
              export <workflow-run-url-or-folder>  Download and trace a GitHub Actions workflow run, or trace a downloaded run-info folder
              download-run-info <url>              Download a single GitHub Actions job and workflow artifacts


            """);
    }

    [Fact]
    public async Task Export_Help_MatchesSnapshot()
    {
        var export = await InvokeCliAsync("export", "--help");

        var actual = $$"""
            exit={{export.ExitCode}}
            {{export.Output}}
            """;

        InlineSnapshot.Validate(actual, """
            exit=0
            Description:
              Download and trace a GitHub Actions workflow run, or trace a downloaded run-info folder

            Usage:
              Meziantou.GitHubActionsTracing.Tests export <workflow-run-url-or-folder> [options]

            Arguments:
              <workflow-run-url-or-folder>  URL of the GitHub Actions workflow run or a downloaded run-info folder

            Options:
              --format <Chromium|Otel|OtelFile|Speedscope>                                    Output format: otel, otel-file, chromium, speedscope
              --otel-endpoint <otel-endpoint>                                                 OpenTelemetry collector endpoint
              --otel-protocol <Grpc|HttpProtobuf>                                             OpenTelemetry protocol: grpc, http, http/protobuf [default: Grpc]
              --otel-file-path, --otel-path <otel-path>                                       Export OpenTelemetry data to a file
              --chromium-path <chromium-path>                                                 Export trace to Chromium format file
              --speedscope-path <speedscope-path>                                             Export trace to Speedscope format file
              --minimum-test-duration <minimum-test-duration>                                 Exclude tests shorter than this duration (e.g. 00:00:01) [default: 00:00:00]
              --minimum-binlog-duration, --minimum-target-duration <minimum-binlog-duration>  Exclude binlog targets shorter than this duration (e.g. 00:00:01) [default: 00:00:00]
              --include-binlog                                                                Include MSBuild binlog targets/tasks in the trace
              --include-tests                                                                 Include TRX/JUnit tests in the trace
              -?, -h, --help                                                                  Show help and usage information


            """);
    }

    [Fact]
    public async Task Export_UsesDownloadedFolder()
    {
        await using var temporaryDirectory = TemporaryDirectory.Create();
        var fixtureDirectory = temporaryDirectory / "fixture";
        ExtractEmbeddedFixture(EmbeddedFixtureFileName, fixtureDirectory);

        var outputPath = temporaryDirectory / "trace.chromium.json";
        var commandResult = await InvokeCliAsync("export", fixtureDirectory.ToString(), "--chromium-path", outputPath.ToString());

        Assert.Equal(0, commandResult.ExitCode);
        Assert.True(File.Exists(outputPath));

        var traceContent = await File.ReadAllTextAsync(outputPath, TestContext.Current.CancellationToken);
        Assert.Contains("GitHub Actions Workflow Run", traceContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DownloadJob_Help_MatchesSnapshot()
    {
        var downloadJob = await InvokeCliAsync("download-run-info", "--help");

        var actual = $$"""
            exit={{downloadJob.ExitCode}}
            {{downloadJob.Output}}
            """;
        InlineSnapshot.Validate(actual.TrimEnd(), """
            exit=0
            Description:
              Download a single GitHub Actions job and workflow artifacts

            Usage:
              Meziantou.GitHubActionsTracing.Tests download-run-info <url> [options]

            Arguments:
              <url>  URL of the GitHub Actions job

            Options:
              --output <output> (REQUIRED)  Destination folder
              -?, -h, --help                Show help and usage information
            """);
    }

    [Fact]
    public async Task EmbeddedFixture_GeneratesValidOtelSpans()
    {
        await using var temporaryDirectory = TemporaryDirectory.Create();
        var fixtureDirectory = temporaryDirectory / "fixture";
        ExtractEmbeddedFixture(EmbeddedFixtureFileName, fixtureDirectory);

        var outputPath = temporaryDirectory / "trace.otel.json";
        var commandResult = await InvokeCliAsync(
            "export",
            fixtureDirectory.ToString(),
            "--otel-path",
            outputPath.ToString(),
            "--include-binlog",
            "--include-tests",
            "--minimum-binlog-duration",
            "00:00:00",
            "--minimum-test-duration",
            "00:00:00");

        Assert.Equal(0, commandResult.ExitCode);

        Assert.True(File.Exists(outputPath));

        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(outputPath, TestContext.Current.CancellationToken));
        var spans = document.RootElement
            .GetProperty("resourceSpans")[0]
            .GetProperty("scopeSpans")[0]
            .GetProperty("spans")
            .EnumerateArray()
            .ToList();

        Assert.NotEmpty(spans);
        Assert.Contains(spans, span => string.Equals(GetAttributeValue(span, "span.kind"), "workflow", StringComparison.Ordinal));
        Assert.Contains(spans, span => string.Equals(GetAttributeValue(span, "span.kind"), "job", StringComparison.Ordinal));
        Assert.Contains(spans, span => string.Equals(GetAttributeValue(span, "span.kind"), "step", StringComparison.Ordinal));
        Assert.Contains(spans, span => string.Equals(GetAttributeValue(span, "span.kind"), "msbuild.target", StringComparison.Ordinal));
        Assert.Contains(spans, span => string.Equals(GetAttributeValue(span, "span.kind"), "test", StringComparison.Ordinal));

        var testSpans = spans
            .Where(span => string.Equals(GetAttributeValue(span, "span.kind"), "test", StringComparison.Ordinal))
            .ToList();

        Assert.All(testSpans, static span =>
        {
            var spanName = span.GetProperty("name").GetString();
            Assert.True(spanName is not null && spanName.StartsWith("Test: ", StringComparison.Ordinal), $"Unexpected test span name '{spanName}'");
        });

        Assert.Contains(spans, span => span.TryGetProperty("events", out _));
        Assert.Contains(spans, span => span.GetProperty("name").GetString()!.Contains("Build and Publish", StringComparison.OrdinalIgnoreCase));

        var spanIds = spans
            .Select(span => span.GetProperty("spanId").GetString())
            .OfType<string>()
            .ToHashSet(StringComparer.Ordinal);

        foreach (var span in spans)
        {
            if (!span.TryGetProperty("parentSpanId", out var parentSpanIdElement) || parentSpanIdElement.ValueKind is JsonValueKind.Null)
                continue;

            var parentSpanId = parentSpanIdElement.GetString();
            if (string.IsNullOrEmpty(parentSpanId))
                continue;

            Assert.Contains(parentSpanId, spanIds);
        }

        var spansById = spans
            .Select(static span => new
            {
                Span = span,
                SpanId = span.GetProperty("spanId").GetString(),
            })
            .Where(static item => !string.IsNullOrEmpty(item.SpanId))
            .ToDictionary(static item => item.SpanId!, static item => item.Span, StringComparer.Ordinal);

        Assert.All(testSpans, span =>
        {
            Assert.True(span.TryGetProperty("parentSpanId", out var parentSpanIdElement) && parentSpanIdElement.ValueKind is JsonValueKind.String,
                "Test span must have a parent span id");

            var parentSpanId = parentSpanIdElement.GetString();
            Assert.NotNull(parentSpanId);
            if (!spansById.TryGetValue(parentSpanId, out var parentSpan))
            {
                Assert.Fail($"Cannot find parent span for test span '{span.GetProperty("name").GetString()}'");
            }

            var parentKind = GetAttributeValue(parentSpan, "span.kind");
            Assert.Equal("msbuild.task", parentKind);
        });

        var expectedHierarchy = new[] { "workflow", "job", "msbuild.target", "msbuild.task", "test" };
        Assert.All(testSpans, span =>
        {
            var hierarchy = GetSpanHierarchyKinds(span, spansById);
            Assert.True(ContainsOrderedSubsequence(hierarchy, expectedHierarchy),
                $"Test span '{span.GetProperty("name").GetString()}' has hierarchy '{string.Join(" / ", hierarchy)}' instead of expected '{string.Join(" / ", expectedHierarchy)}'.");
        });

        var rootHelpSpan = testSpans.Single(span =>
        {
            var name = span.GetProperty("name").GetString();
            return name is not null && name.EndsWith(".Root_Help_MatchesSnapshot", StringComparison.Ordinal);
        });

        var rootHelpSpanName = rootHelpSpan.GetProperty("name").GetString();
        Assert.NotNull(rootHelpSpanName);
        Assert.EndsWith("Root_Help_MatchesSnapshot", rootHelpSpanName, StringComparison.Ordinal);

        var rootHelpHierarchyNames = GetSpanHierarchyNames(rootHelpSpan, spansById);
        Assert.Equal(
        [
            "Build and Publish",
            "build",
            "VSTest",
            "VSTestTask",
            rootHelpSpanName,
        ],
        rootHelpHierarchyNames);
    }

    [Fact]
    public async Task EmbeddedFixture_GeneratesValidChromiumTrace()
    {
        await using var temporaryDirectory = TemporaryDirectory.Create();
        var model = LoadEmbeddedFixtureModel(temporaryDirectory);

        var outputPath = temporaryDirectory / "nested" / "trace.chromium.json";
        await new ChromiumTraceExporter(outputPath).ExportAsync(model);

        Assert.True(File.Exists(outputPath));

        var fileContent = await File.ReadAllTextAsync(outputPath, TestContext.Current.CancellationToken);
        Assert.Contains("GitHub Actions Workflow Run", fileContent, StringComparison.Ordinal);

        using var document = JsonDocument.Parse(fileContent);
        var traceEvents = GetTraceEvents(document.RootElement);
        Assert.NotEmpty(traceEvents);

        var completeEvents = traceEvents
            .Where(IsChromiumCompleteEvent)
            .ToList();

        Assert.Equal(model.Spans.Count, completeEvents.Count);

        var exportedKinds = completeEvents
            .Select(evt => GetJsonString(evt, "cat", "category"))
            .OfType<string>()
            .ToHashSet(StringComparer.Ordinal);

        foreach (var kind in model.Spans.Select(static span => span.Kind).Distinct(StringComparer.Ordinal))
        {
            Assert.True(exportedKinds.Contains(kind), $"Missing Chromium event category '{kind}'");
        }

        Assert.Contains(completeEvents, static evt => GetJsonInt32(evt, "tid", "threadId") is 1);

        Assert.Contains(completeEvents, static evt =>
            GetJsonString(evt, "cat", "category") is "job" &&
            evt.TryGetProperty("args", out var args) &&
            args.TryGetProperty("kind", out var kind) &&
            string.Equals(kind.GetString(), "job", StringComparison.Ordinal) &&
            args.TryGetProperty("events.count", out var eventCount) &&
            eventCount.ValueKind is not JsonValueKind.Null);
    }

    [Fact]
    public async Task EmbeddedFixture_GeneratesValidSpeedscopeTrace()
    {
        await using var temporaryDirectory = TemporaryDirectory.Create();
        var model = LoadEmbeddedFixtureModel(temporaryDirectory);

        var outputPath = temporaryDirectory / "nested" / "trace.speedscope.json";
        await new SpeedscopeTraceExporter(outputPath).ExportAsync(model);

        Assert.True(File.Exists(outputPath));

        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(outputPath, TestContext.Current.CancellationToken));
        var root = document.RootElement;

        Assert.Equal("https://www.speedscope.app/file-format-schema.json", root.GetProperty("$schema").GetString());
        Assert.Equal($"GitHub run {model.WorkflowRunId}", root.GetProperty("name").GetString());

        var frames = root.GetProperty("shared").GetProperty("frames").EnumerateArray().ToList();
        Assert.NotEmpty(frames);
        Assert.All(frames, static frame => Assert.False(string.IsNullOrWhiteSpace(frame.GetProperty("name").GetString())));

        var profiles = root.GetProperty("profiles").EnumerateArray().ToList();
        var expectedProfileCount = model.Spans.Count(static span => span.Kind is "job");
        Assert.Equal(expectedProfileCount, profiles.Count);

        foreach (var profile in profiles)
        {
            Assert.Equal("evented", profile.GetProperty("type").GetString());
            Assert.Equal("milliseconds", profile.GetProperty("unit").GetString());
            Assert.Equal(0, profile.GetProperty("startValue").GetDouble());
            Assert.True(profile.GetProperty("endValue").GetDouble() >= 1);

            var events = profile.GetProperty("events").EnumerateArray().ToList();
            Assert.NotEmpty(events);
            Assert.True(events.Count % 2 is 0);

            var previousAt = double.MinValue;
            string? previousType = null;

            foreach (var evt in events)
            {
                var at = evt.GetProperty("at").GetDouble();
                var type = evt.GetProperty("type").GetString();
                var frameIndex = evt.GetProperty("frame").GetInt32();

                Assert.True(type is "O" or "C");
                Assert.InRange(frameIndex, 0, frames.Count - 1);

                Assert.True(at >= previousAt);
                if (at == previousAt)
                {
                    Assert.False(string.Equals(previousType, "O", StringComparison.Ordinal) && string.Equals(type, "C", StringComparison.Ordinal));
                }

                previousAt = at;
                previousType = type;
            }
        }
    }

    private static void ExtractEmbeddedFixture(string fileName, FullPath outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);

        var assembly = typeof(CliApplicationTests).Assembly;
        var resourceName = assembly
            .GetManifestResourceNames()
            .Single(name => name.EndsWith(fileName, StringComparison.Ordinal));

        using var resourceStream = assembly.GetManifestResourceStream(resourceName);
        Assert.NotNull(resourceStream);

        using var archive = new ZipArchive(resourceStream, ZipArchiveMode.Read);
        archive.ExtractToDirectory(outputDirectory, overwriteFiles: true);
    }

    private static TraceModel LoadEmbeddedFixtureModel(FullPath temporaryDirectory)
    {
        var fixtureDirectory = temporaryDirectory / "fixture";
        ExtractEmbeddedFixture(EmbeddedFixtureFileName, fixtureDirectory);

        return TraceModel.Load(fixtureDirectory, new TraceLoadOptions
        {
            IncludeBinlog = true,
            IncludeTests = true,
            MinimumBinlogDuration = TimeSpan.Zero,
            MinimumTestDuration = TimeSpan.Zero,
        });
    }

    private static List<JsonElement> GetTraceEvents(JsonElement root)
    {
        if (root.ValueKind is JsonValueKind.Array)
            return [.. root.EnumerateArray()];

        if (root.TryGetProperty("traceEvents", out var traceEvents) && traceEvents.ValueKind is JsonValueKind.Array)
            return [.. traceEvents.EnumerateArray()];

        throw new InvalidOperationException("Cannot find 'traceEvents' in Chromium trace output.");
    }

    private static bool IsChromiumCompleteEvent(JsonElement element)
    {
        var phase = GetJsonString(element, "ph", "phase");
        if (phase is not null)
            return string.Equals(phase, "X", StringComparison.Ordinal);

        return element.TryGetProperty("dur", out _) || element.TryGetProperty("duration", out _);
    }

    private static string? GetJsonString(JsonElement element, params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            if (!element.TryGetProperty(propertyName, out var value))
                continue;

            if (value.ValueKind is JsonValueKind.String)
                return value.GetString();
        }

        return null;
    }

    private static int? GetJsonInt32(JsonElement element, params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            if (!element.TryGetProperty(propertyName, out var value))
                continue;

            if (value.ValueKind is JsonValueKind.Number && value.TryGetInt32(out var result))
                return result;
        }

        return null;
    }

    private static List<string> GetSpanHierarchyKinds(JsonElement span, Dictionary<string, JsonElement> spansById)
    {
        var result = new List<string>();
        var current = span;

        while (true)
        {
            var kind = GetAttributeValue(current, "span.kind");
            if (kind is not null)
            {
                result.Add(kind);
            }

            if (!current.TryGetProperty("parentSpanId", out var parentSpanIdElement) || parentSpanIdElement.ValueKind is JsonValueKind.Null)
                break;

            var parentSpanId = parentSpanIdElement.GetString();
            if (string.IsNullOrEmpty(parentSpanId) || !spansById.TryGetValue(parentSpanId, out current))
                break;
        }

        result.Reverse();
        return result;
    }

    private static List<string> GetSpanHierarchyNames(JsonElement span, Dictionary<string, JsonElement> spansById)
    {
        var result = new List<string>();
        var current = span;

        while (true)
        {
            var name = current.GetProperty("name").GetString();
            if (!string.IsNullOrEmpty(name))
            {
                result.Add(name);
            }

            if (!current.TryGetProperty("parentSpanId", out var parentSpanIdElement) || parentSpanIdElement.ValueKind is JsonValueKind.Null)
                break;

            var parentSpanId = parentSpanIdElement.GetString();
            if (string.IsNullOrEmpty(parentSpanId) || !spansById.TryGetValue(parentSpanId, out current))
                break;
        }

        result.Reverse();
        return result;
    }

    private static bool ContainsOrderedSubsequence(IReadOnlyList<string> source, string[] expected)
    {
        var expectedIndex = 0;

        foreach (var item in source)
        {
            if (!string.Equals(item, expected[expectedIndex], StringComparison.Ordinal))
                continue;

            expectedIndex++;
            if (expectedIndex == expected.Length)
                return true;
        }

        return false;
    }

    private static string? GetAttributeValue(JsonElement span, string key)
    {
        if (!span.TryGetProperty("attributes", out var attributes) || attributes.ValueKind != JsonValueKind.Array)
            return null;

        foreach (var attribute in attributes.EnumerateArray())
        {
            if (!attribute.TryGetProperty("key", out var keyElement) || !string.Equals(keyElement.GetString(), key, StringComparison.Ordinal))
                continue;

            if (!attribute.TryGetProperty("value", out var valueElement))
                return null;

            if (valueElement.TryGetProperty("stringValue", out var stringValue))
                return stringValue.GetString();

            if (valueElement.TryGetProperty("intValue", out var intValue))
                return intValue.GetString();

            if (valueElement.TryGetProperty("doubleValue", out var doubleValue))
                return doubleValue.GetRawText();

            if (valueElement.TryGetProperty("boolValue", out var boolValue))
                return boolValue.GetRawText();

            return valueElement.GetRawText();
        }

        return null;
    }

    private static async Task<CommandResult> InvokeCliAsync(params string[] args)
    {
        await ConsoleSemaphore.WaitAsync(TestContext.Current.CancellationToken);
        try
        {
            using var standardOutput = new StringWriter();
            using var standardError = new StringWriter();
            var previousOut = Console.Out;
            var previousError = Console.Error;
            try
            {
                Console.SetOut(standardOutput);
                Console.SetError(standardError);
                var exitCode = await CliApplication.RunAsync(args);
                return new CommandResult(exitCode, standardOutput.ToString(), standardError.ToString());
            }
            finally
            {
                Console.SetOut(previousOut);
                Console.SetError(previousError);
            }
        }
        finally
        {
            ConsoleSemaphore.Release();
        }
    }

    private sealed record CommandResult(int ExitCode, string StandardOutput, string StandardError)
    {
        public string Output => StandardOutput + StandardError;
    }
}
