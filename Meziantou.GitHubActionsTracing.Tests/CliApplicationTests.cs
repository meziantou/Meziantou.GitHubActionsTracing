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
              export <workflow-run-url-or-folder>  Download and trace a GitHub Actions workflow run, or trace a downloaded run-info folder or zip file
              download-run-info <url>              Download GitHub Actions workflow run info and artifacts


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
              Download and trace a GitHub Actions workflow run, or trace a downloaded run-info folder or zip file

            Usage:
              Meziantou.GitHubActionsTracing.Tests export <workflow-run-url-or-folder> [options]

            Arguments:
              <workflow-run-url-or-folder>  URL of the GitHub Actions workflow run, a downloaded run-info folder, or a zip file

            Options:
              --format <Chromium|Html|Otel|OtelFile|Speedscope>                               Output format: otel, otel-file, chromium, speedscope, html
              --otel-endpoint <otel-endpoint>                                                 OpenTelemetry collector endpoint
              --otel-protocol <Grpc|HttpProtobuf>                                             OpenTelemetry protocol: grpc, http, http/protobuf [default: Grpc]
              --otel-file-path <otel-file-path>                                               Export OpenTelemetry data to a file
              --chromium-path <chromium-path>                                                 Export trace to Chromium format file
              --speedscope-path <speedscope-path>                                             Export trace to Speedscope format file
              --html-path <html-path>                                                         Export trace to HTML file with interactive swimlanes
              --minimum-test-duration <minimum-test-duration>                                 Exclude tests shorter than this duration (e.g. 00:00:01) [default: 00:00:00]
              --minimum-binlog-duration, --minimum-target-duration <minimum-binlog-duration>  Exclude binlog targets shorter than this duration (e.g. 00:00:01) [default: 00:00:00]
              --include-binlog                                                                Include MSBuild binlog targets/tasks in the trace
              --include-tests                                                                 Include TRX/JUnit tests in the trace
              -?, -h, --help                                                                  Show help and usage information


            """);
    }

    [Theory]
    [InlineData("meziantou/Meziantou.GitHubActionsTracing", true)]
    [InlineData("meziantou/another-repository", false)]
    public void GitHubRepositoryFilter_ExactMatch(string repositoryFullName, bool expected)
    {
        var filter = new GitHubRepositoryFilter(
            allowedRepositoriesExact: ["meziantou/Meziantou.GitHubActionsTracing"],
            allowedRepositoriesPatterns: []);

        Assert.Equal(expected, filter.IsAllowed(repositoryFullName));
    }

    [Fact]
    public void GitHubRepositoryFilter_InvalidExactRepositories_AreIgnored()
    {
        var filter = new GitHubRepositoryFilter(
            allowedRepositoriesExact: ["invalid", "owner/", "/repo", "owner/repo/extra", " owner/repo "],
            allowedRepositoriesPatterns: []);

        Assert.True(filter.IsEnabled);
        Assert.True(filter.IsAllowed("owner/repo"));
        Assert.False(filter.IsAllowed("owner/other"));
    }

    [Theory]
    [InlineData("meziantou/repo1", true)]
    [InlineData("sample/abc-test", true)]
    [InlineData("sample/def", false)]
    public void GitHubRepositoryFilter_PatternMatch(string repositoryFullName, bool expected)
    {
        var filter = new GitHubRepositoryFilter(
            allowedRepositoriesExact: [],
            allowedRepositoriesPatterns: ["^meziantou/(.*)$", "^sample/abc-"]);

        Assert.Equal(expected, filter.IsAllowed(repositoryFullName));
    }

    [Fact]
    public void GitHubRepositoryFilter_WithoutRules_AllowsEverything()
    {
        var filter = new GitHubRepositoryFilter(
            allowedRepositoriesExact: [],
            allowedRepositoriesPatterns: []);

        Assert.True(filter.IsAllowed("meziantou/repo1"));
        Assert.True(filter.IsAllowed("sample/repo2"));
    }

    [Fact]
    public void GitHubRepositoryFilter_InvalidPattern_Throws()
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            new GitHubRepositoryFilter(
                allowedRepositoriesExact: [],
                allowedRepositoriesPatterns: ["("]));

        Assert.Contains("Invalid repository pattern", exception.Message, StringComparison.Ordinal);
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
    public async Task Export_UsesZipFile()
    {
        await using var temporaryDirectory = TemporaryDirectory.Create();
        var zipPath = temporaryDirectory / "run-info.zip";

        var assembly = typeof(CliApplicationTests).Assembly;
        var resourceName = assembly
            .GetManifestResourceNames()
            .Single(name => name.EndsWith(EmbeddedFixtureFileName, StringComparison.Ordinal));

        using (var resourceStream = assembly.GetManifestResourceStream(resourceName))
        {
            Assert.NotNull(resourceStream);
            using var fileStream = File.Create(zipPath);
            await resourceStream.CopyToAsync(fileStream, TestContext.Current.CancellationToken);
        }

        var outputPath = temporaryDirectory / "trace.chromium.json";
        var commandResult = await InvokeCliAsync("export", zipPath.ToString(), "--chromium-path", outputPath.ToString());

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
              Download GitHub Actions workflow run info and artifacts

            Usage:
              Meziantou.GitHubActionsTracing.Tests download-run-info <url> [options]

            Arguments:
              <url>  URL of the GitHub Actions workflow run

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
            "--otel-file-path",
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
        Assert.Contains(spans, span => string.Equals(GetAttributeValue(span, "span.kind"), "log.group", StringComparison.Ordinal));
        Assert.Contains(spans, span => string.Equals(GetAttributeValue(span, "span.kind"), "msbuild.project", StringComparison.Ordinal));
        Assert.Contains(spans, span => string.Equals(GetAttributeValue(span, "span.kind"), "msbuild.target", StringComparison.Ordinal));
        Assert.Contains(spans, span => string.Equals(GetAttributeValue(span, "span.kind"), "test", StringComparison.Ordinal));

        var disableAutomaticGarbageCollectionGroup = spans.Single(span =>
            string.Equals(span.GetProperty("name").GetString(), "Disabling automatic garbage collection", StringComparison.Ordinal) &&
            string.Equals(GetAttributeValue(span, "span.kind"), "log.group", StringComparison.Ordinal));

        var testSpans = spans
            .Where(span => string.Equals(GetAttributeValue(span, "span.kind"), "test", StringComparison.Ordinal))
            .ToList();

        Assert.All(testSpans, span =>
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
            .Select(span => new
            {
                Span = span,
                SpanId = span.GetProperty("spanId").GetString(),
            })
            .Where(item => !string.IsNullOrEmpty(item.SpanId))
            .ToDictionary(item => item.SpanId!, item => item.Span, StringComparer.Ordinal);

        var targetSpans = spans
            .Where(span => string.Equals(GetAttributeValue(span, "span.kind"), "msbuild.target", StringComparison.Ordinal))
            .ToList();

        Assert.NotEmpty(targetSpans);
        Assert.Contains(targetSpans, targetSpan =>
        {
            if (!targetSpan.TryGetProperty("parentSpanId", out var parentSpanIdElement) || parentSpanIdElement.ValueKind is not JsonValueKind.String)
                return false;

            var parentSpanId = parentSpanIdElement.GetString();
            if (string.IsNullOrEmpty(parentSpanId) || !spansById.TryGetValue(parentSpanId, out var parentSpan))
                return false;

            return string.Equals(GetAttributeValue(parentSpan, "span.kind"), "msbuild.project", StringComparison.Ordinal);
        });

        Assert.True(disableAutomaticGarbageCollectionGroup.TryGetProperty("parentSpanId", out var groupParentSpanIdElement) && groupParentSpanIdElement.ValueKind is JsonValueKind.String,
            "Group span must have a parent span id");

        var groupParentSpanId = groupParentSpanIdElement.GetString();
        Assert.NotNull(groupParentSpanId);

        Assert.True(spansById.TryGetValue(groupParentSpanId, out var groupParentSpan),
            $"Cannot find parent span for group span '{disableAutomaticGarbageCollectionGroup.GetProperty("name").GetString()}'");

        Assert.Equal("step", GetAttributeValue(groupParentSpan, "span.kind"));

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

        var expectedHierarchy = new[] { "workflow", "job", "msbuild.project", "msbuild.target", "msbuild.task", "test" };
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

        var rootHelpHierarchyKinds = GetSpanHierarchyKinds(rootHelpSpan, spansById);
        Assert.True(ContainsOrderedSubsequence(rootHelpHierarchyKinds, expectedHierarchy),
            $"Test span '{rootHelpSpanName}' has hierarchy '{string.Join(" / ", rootHelpHierarchyKinds)}' instead of expected '{string.Join(" / ", expectedHierarchy)}'.");

        var rootHelpHierarchyNames = GetSpanHierarchyNames(rootHelpSpan, spansById);
        Assert.True(ContainsOrderedSubsequence(rootHelpHierarchyNames,
        [
            "Build and Publish",
            "build",
            "VSTest",
            "VSTestTask",
            rootHelpSpanName,
        ]));
    }

    [Fact]
    public async Task Export_FormatOtel_UsesExporterPrefixedOtelEnvironmentVariables()
    {
        await using var temporaryDirectory = TemporaryDirectory.Create();
        var fixtureDirectory = temporaryDirectory / "fixture";
        ExtractEmbeddedFixture(EmbeddedFixtureFileName, fixtureDirectory);

        using var _ = new EnvironmentVariableScope(new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["OTEL_EXPORTER_OTLP_ENDPOINT"] = "invalid://not-used",
            ["EXPORTER_OTEL_EXPORTER_OTLP_ENDPOINT"] = "http://127.0.0.1:4317",
        });

        var commandResult = await InvokeCliAsync(
            "export",
            fixtureDirectory.ToString(),
            "--format",
            "otel");

        Assert.Equal(0, commandResult.ExitCode);
        Assert.DoesNotContain("invalid://not-used", commandResult.Output, StringComparison.Ordinal);
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

        foreach (var kind in model.Spans.Select(span => span.Kind).Distinct(StringComparer.Ordinal))
        {
            Assert.True(exportedKinds.Contains(kind), $"Missing Chromium event category '{kind}'");
        }

        Assert.Contains(completeEvents, evt => GetJsonInt32(evt, "tid", "threadId") is 1);

        Assert.Contains(completeEvents, evt =>
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
        Assert.All(frames, frame => Assert.False(string.IsNullOrWhiteSpace(frame.GetProperty("name").GetString())));

        var profiles = root.GetProperty("profiles").EnumerateArray().ToList();
        var expectedProfileCount = model.Spans.Count(span => span.Kind is "job");
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

    [Fact]
    public async Task EmbeddedFixture_GeneratesHtmlWithPanAndZoomWheelControls()
    {
        await using var temporaryDirectory = TemporaryDirectory.Create();
        var model = LoadEmbeddedFixtureModel(temporaryDirectory);

        var outputPath = temporaryDirectory / "nested" / "trace.html";
        await new HtmlTraceExporter(outputPath).ExportAsync(model);

        Assert.True(File.Exists(outputPath));

        var fileContent = await File.ReadAllTextAsync(outputPath, TestContext.Current.CancellationToken);
        Assert.Contains("if (e.ctrlKey || e.metaKey)", fileContent, StringComparison.Ordinal);
        Assert.Contains("panX -= horizontalDelta;", fileContent, StringComparison.Ordinal);
        Assert.Contains("panY -= verticalDelta;", fileContent, StringComparison.Ordinal);
        Assert.Contains("function clampPan()", fileContent, StringComparison.Ordinal);
        Assert.Contains("const minPanX = timelineWidth - dataWidth;", fileContent, StringComparison.Ordinal);
        Assert.Contains("const minPanY = viewportLaneHeight - dataHeight;", fileContent, StringComparison.Ordinal);
        Assert.Contains("Show MSBuild Targets", fileContent, StringComparison.Ordinal);
        Assert.Contains("Show MSBuild Tasks", fileContent, StringComparison.Ordinal);
        Assert.Contains("Show Tests", fileContent, StringComparison.Ordinal);
        Assert.Contains("function isSpanVisibleByFilters(span)", fileContent, StringComparison.Ordinal);
        Assert.Contains("span.kind === 'msbuild.target'", fileContent, StringComparison.Ordinal);
        Assert.Contains("span.kind === 'msbuild.task'", fileContent, StringComparison.Ordinal);
        Assert.Contains("span.kind === 'test'", fileContent, StringComparison.Ordinal);
        Assert.Contains("max-height: calc(100vh - 16px);", fileContent, StringComparison.Ordinal);
        Assert.Contains("overflow-wrap: anywhere;", fileContent, StringComparison.Ordinal);
        Assert.Contains("function positionTooltip(mouseX, mouseY)", fileContent, StringComparison.Ordinal);
        Assert.Contains("positionTooltip(e.clientX, e.clientY);", fileContent, StringComparison.Ordinal);
        Assert.Contains("const spansById = new Map();", fileContent, StringComparison.Ordinal);
        Assert.Contains("function getSpanHierarchy(span)", fileContent, StringComparison.Ordinal);
        Assert.Contains("function formatUtcTimestamp(epochMilliseconds)", fileContent, StringComparison.Ordinal);
        Assert.Contains("tooltip-label'>Hierarchy:</span>", fileContent, StringComparison.Ordinal);
        Assert.Contains("tooltip-label'>Start time (UTC):</span>", fileContent, StringComparison.Ordinal);
        Assert.Contains("tooltip-label'>End time (UTC):</span>", fileContent, StringComparison.Ordinal);
        Assert.Contains("id=\"details-panel\"", fileContent, StringComparison.Ordinal);
        Assert.Contains("id=\"details-panel-resizer\"", fileContent, StringComparison.Ordinal);
        Assert.Contains("id=\"details-panel-content\"", fileContent, StringComparison.Ordinal);
        Assert.Contains("openDetailsPanel(span);", fileContent, StringComparison.Ordinal);
        Assert.Contains("canvas.addEventListener('click'", fileContent, StringComparison.Ordinal);
        Assert.Contains("navigator.clipboard.writeText", fileContent, StringComparison.Ordinal);
        Assert.Contains("user-select: text;", fileContent, StringComparison.Ordinal);
        Assert.Contains("View run on GitHub", fileContent, StringComparison.Ordinal);
        Assert.Contains(model.WorkflowRun.HtmlUrl, fileContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EmbeddedFixture_BinlogSpanNames_AreContextualAndCallTargetFiltered()
    {
        await using var temporaryDirectory = TemporaryDirectory.Create();
        var model = LoadEmbeddedFixtureModel(temporaryDirectory);

        Assert.DoesNotContain(model.Spans, span =>
            span.Kind is "msbuild.target" or "msbuild.task"
            && string.Equals(span.Name, "CallTarget", StringComparison.OrdinalIgnoreCase));

        Assert.DoesNotContain(model.Spans, span =>
            span.Kind is "msbuild.target" or "msbuild.task"
            && string.Equals(span.Name, "Csc", StringComparison.OrdinalIgnoreCase));

        Assert.DoesNotContain(model.Spans, span =>
            span.Kind is "msbuild.target" or "msbuild.task"
            && string.Equals(span.Name, "Restore", StringComparison.OrdinalIgnoreCase));

        Assert.DoesNotContain(model.Spans, span =>
            span.Kind is "msbuild.task"
            && string.Equals(span.Name, "RestoreTask", StringComparison.OrdinalIgnoreCase));

        Assert.Contains(model.Spans, span =>
            span.Kind is "msbuild.target" or "msbuild.task"
            && span.Name.StartsWith("Csc (", StringComparison.Ordinal));

        Assert.Contains(model.Spans, span =>
            span.Kind is "msbuild.target" or "msbuild.task"
            && span.Name.StartsWith("Restore (", StringComparison.Ordinal));

        Assert.Contains(model.Spans, span =>
            span.Kind is "msbuild.task"
            && span.Name.StartsWith("RestoreTask (", StringComparison.Ordinal));
    }

    [Fact]
    public async Task EmbeddedFixture_BinlogTaskParameters_AreAddedToTaskSpans()
    {
        await using var temporaryDirectory = TemporaryDirectory.Create();
        var model = LoadEmbeddedFixtureModel(temporaryDirectory);

        var taskSpans = model.Spans
            .Where(span => span.Kind is "msbuild.task")
            .ToList();

        Assert.NotEmpty(taskSpans);

        Assert.Contains(taskSpans, span =>
            span.Attributes.Any(kvp =>
                kvp.Key.StartsWith("task.parameter.", StringComparison.Ordinal)
                && kvp.Value is string parameterValue
                && !string.IsNullOrWhiteSpace(parameterValue)));
    }

    [Fact]
    public async Task EmbeddedFixture_CscTask_HasCommandLineArgumentsAsTaskParameter()
    {
        await using var temporaryDirectory = TemporaryDirectory.Create();
        var model = LoadEmbeddedFixtureModel(temporaryDirectory);

        var cscTaskSpans = model.Spans
            .Where(span => span.Kind is "msbuild.task" && span.Name.StartsWith("Csc (", StringComparison.Ordinal))
            .ToList();

        Assert.NotEmpty(cscTaskSpans);

        Assert.Contains(cscTaskSpans, span =>
            span.Attributes.TryGetValue("task.parameter.CommandLineArguments", out var value)
            && value is string commandLineArguments
            && !string.IsNullOrWhiteSpace(commandLineArguments));
    }

    [Fact]
    public async Task Load_RecomputesJobAndStepDurationsFromChildren()
    {
        await using var temporaryDirectory = TemporaryDirectory.Create();
        var fixtureDirectory = temporaryDirectory / "fixture";
        CreateRunInfoFixtureWithPreciseGroupOutsideStepDuration(fixtureDirectory);

        var model = TraceModel.Load(fixtureDirectory);

        var job = model.Spans.Single(span => span.Kind is "job");
        var steps = model.Spans
            .Where(span => span.Kind is "step")
            .OrderBy(span => span.Attributes.TryGetValue("step.number", out var value) && value is int stepNumber ? stepNumber : int.MaxValue)
            .ToList();
        var groups = model.Spans
            .Where(span => span.Kind is "log.group")
            .ToList();

        Assert.Equal(2, steps.Count);
        Assert.Equal(2, groups.Count);

        var firstStep = steps[0];
        var secondStep = steps[1];
        var firstGroup = groups.Single(span => span.Name == "Precise log group");
        var secondGroup = groups.Single(span => span.Name == "Group currently mapped to next step");

        Assert.Equal(firstStep.Id, firstGroup.ParentId);
        Assert.Equal(firstGroup.EndTime, firstStep.EndTime);
        Assert.Equal(firstStep.EndTime, secondStep.StartTime);
        Assert.Equal(secondStep.EndTime, job.EndTime);
        Assert.True(firstStep.StartTime <= firstGroup.StartTime);
        Assert.True(secondGroup.StartTime < secondStep.StartTime);
    }

    [Fact]
    public async Task Load_MapsArtifactToUploadArtifactGroupJob()
    {
        await using var temporaryDirectory = TemporaryDirectory.Create();
        var fixtureDirectory = temporaryDirectory / "fixture";
        CreateRunInfoFixtureWithUploadArtifactGroup(fixtureDirectory);

        var model = TraceModel.Load(fixtureDirectory, new TraceLoadOptions
        {
            IncludeTests = true,
            MinimumTestDuration = TimeSpan.Zero,
        });

        var testSpan = model.Spans.Single(span => span.Kind is "test");
        Assert.Equal(1001, testSpan.JobId);
    }

    [Fact]
    public async Task Load_ParsesWarningAndErrorFromWorkflowCommandsLegacySyntaxAndAnsiColors()
    {
        await using var temporaryDirectory = TemporaryDirectory.Create();
        var fixtureDirectory = temporaryDirectory / "fixture";
        CreateRunInfoFixtureWithAnnotationsAndAnsiColors(fixtureDirectory);

        var model = TraceModel.Load(fixtureDirectory);

        var stepSpan = model.Spans.Single(span => span.Kind is "step");
        var events = stepSpan.Events;

        Assert.Equal(6, events.Count);

        Assert.Contains(events, traceEvent =>
            traceEvent.Name is "warning"
            && traceEvent.Message is "warning from workflow command");

        Assert.Contains(events, traceEvent =>
            traceEvent.Name is "error"
            && traceEvent.Message is "error from workflow command");

        Assert.Contains(events, traceEvent =>
            traceEvent.Name is "warning"
            && traceEvent.Message is "warning from legacy syntax");

        Assert.Contains(events, traceEvent =>
            traceEvent.Name is "error"
            && traceEvent.Message is "error from legacy syntax");

        Assert.Contains(events, traceEvent =>
            traceEvent.Name is "warning"
            && traceEvent.Message is "warning from ansi orange"
            && traceEvent.Attributes.TryGetValue("annotation.source", out var source)
            && source is "ansi");

        Assert.Contains(events, traceEvent =>
            traceEvent.Name is "error"
            && traceEvent.Message is "error from ansi red"
            && traceEvent.Attributes.TryGetValue("annotation.source", out var source)
            && source is "ansi");

        Assert.DoesNotContain(events, traceEvent =>
            traceEvent.Message.Contains("cyan informational output", StringComparison.Ordinal));
    }

    private static void CreateRunInfoFixtureWithAnnotationsAndAnsiColors(FullPath fixtureDirectory)
    {
        var metadataDirectory = fixtureDirectory / "metadata";
        var logsDirectory = fixtureDirectory / "logs" / "jobs";

        Directory.CreateDirectory(metadataDirectory);
        Directory.CreateDirectory(logsDirectory);

        File.WriteAllText(metadataDirectory / "run.json", """
                        {
                            "id": 42,
                            "name": "Sample workflow",
                            "created_at": "2026-02-16T00:00:00Z",
                            "run_started_at": "2026-02-16T00:00:00Z",
                            "updated_at": "2026-02-16T00:00:10Z"
                        }
                        """);

        File.WriteAllText(metadataDirectory / "jobs.json", """
                        {
                            "jobs": [
                                {
                                    "id": 1001,
                                    "run_id": 42,
                                    "name": "build",
                                    "status": "completed",
                                    "conclusion": "success",
                                    "created_at": "2026-02-16T00:00:00Z",
                                    "started_at": "2026-02-16T00:00:00Z",
                                    "completed_at": "2026-02-16T00:00:10Z",
                                    "steps": [
                                        {
                                            "number": 1,
                                            "name": "build step",
                                            "status": "completed",
                                            "conclusion": "success",
                                            "started_at": "2026-02-16T00:00:00Z",
                                            "completed_at": "2026-02-16T00:00:10Z"
                                        }
                                    ]
                                }
                            ]
                        }
                        """);

        File.WriteAllText(metadataDirectory / "artifacts.json", """
                        {
                            "artifacts": []
                        }
                        """);

        File.WriteAllText(logsDirectory / "1001.log", string.Join(Environment.NewLine,
        [
            "2026-02-16T00:00:01.0000000Z ::warning::warning from workflow command",
            "2026-02-16T00:00:02.0000000Z ::error::error from workflow command",
            "2026-02-16T00:00:03.0000000Z ##[warning]warning from legacy syntax",
            "2026-02-16T00:00:04.0000000Z ##[error]error from legacy syntax",
            "2026-02-16T00:00:05.0000000Z \u001b[38;5;208mwarning from ansi orange\u001b[0m",
            "2026-02-16T00:00:06.0000000Z \u001b[31;1merror from ansi red\u001b[0m",
            "2026-02-16T00:00:07.0000000Z \u001b[36;1mcyan informational output\u001b[0m",
        ]) + Environment.NewLine);
    }

    private static void CreateRunInfoFixtureWithPreciseGroupOutsideStepDuration(FullPath fixtureDirectory)
    {
        var metadataDirectory = fixtureDirectory / "metadata";
        var logsDirectory = fixtureDirectory / "logs" / "jobs";

        Directory.CreateDirectory(metadataDirectory);
        Directory.CreateDirectory(logsDirectory);

        File.WriteAllText(metadataDirectory / "run.json", """
                        {
                            "id": 42,
                            "name": "Sample workflow",
                            "created_at": "2026-02-16T00:00:00Z",
                            "run_started_at": "2026-02-16T00:00:00Z",
                            "updated_at": "2026-02-16T00:00:03Z"
                        }
                        """);

        File.WriteAllText(metadataDirectory / "jobs.json", """
                        {
                            "jobs": [
                                {
                                    "id": 1001,
                                    "run_id": 42,
                                    "name": "build",
                                    "status": "completed",
                                    "conclusion": "success",
                                    "created_at": "2026-02-16T00:00:00Z",
                                    "started_at": "2026-02-16T00:00:00Z",
                                    "completed_at": "2026-02-16T00:00:03Z",
                                    "steps": [
                                        {
                                            "number": 1,
                                            "name": "build step",
                                            "status": "completed",
                                            "conclusion": "success",
                                            "started_at": "2026-02-16T00:00:01Z",
                                            "completed_at": "2026-02-16T00:00:02Z"
                                        },
                                        {
                                            "number": 2,
                                            "name": "test step",
                                            "status": "completed",
                                            "conclusion": "success",
                                            "started_at": "2026-02-16T00:00:02Z",
                                            "completed_at": "2026-02-16T00:00:03Z"
                                        }
                                    ]
                                }
                            ]
                        }
                        """);

        File.WriteAllText(metadataDirectory / "artifacts.json", """
                        {
                            "artifacts": []
                        }
                        """);

        File.WriteAllText(logsDirectory / "1001.log", """
                        2026-02-16T00:00:01.9000000Z ::group::Precise log group
                        2026-02-16T00:00:02.1500000Z ::endgroup::
                        2026-02-16T00:00:02.0500000Z ::group::Group currently mapped to next step
                        2026-02-16T00:00:02.0600000Z ::endgroup::
                        """);
    }

    private static void CreateRunInfoFixtureWithUploadArtifactGroup(FullPath fixtureDirectory)
    {
        var metadataDirectory = fixtureDirectory / "metadata";
        var logsDirectory = fixtureDirectory / "logs" / "jobs";
        var artifactDirectory = fixtureDirectory / "artifacts" / "5001-build-tests-artifacts" / "files";

        Directory.CreateDirectory(metadataDirectory);
        Directory.CreateDirectory(logsDirectory);
        Directory.CreateDirectory(artifactDirectory);

        File.WriteAllText(metadataDirectory / "run.json", """
                        {
                            "id": 42,
                            "name": "Sample workflow",
                            "created_at": "2026-02-16T00:00:00Z",
                            "run_started_at": "2026-02-16T00:00:00Z",
                            "updated_at": "2026-02-16T00:12:00Z"
                        }
                        """);

        File.WriteAllText(metadataDirectory / "jobs.json", """
                        {
                            "jobs": [
                                {
                                    "id": 1001,
                                    "run_id": 42,
                                    "name": "build",
                                    "status": "completed",
                                    "conclusion": "success",
                                    "created_at": "2026-02-16T00:00:00Z",
                                    "started_at": "2026-02-16T00:00:00Z",
                                    "completed_at": "2026-02-16T00:02:00Z",
                                    "steps": [
                                        {
                                            "number": 1,
                                            "name": "upload artifact",
                                            "status": "completed",
                                            "conclusion": "success",
                                            "started_at": "2026-02-16T00:00:00Z",
                                            "completed_at": "2026-02-16T00:02:00Z"
                                        }
                                    ]
                                },
                                {
                                    "id": 1002,
                                    "run_id": 42,
                                    "name": "publish",
                                    "status": "completed",
                                    "conclusion": "success",
                                    "created_at": "2026-02-16T00:10:00Z",
                                    "started_at": "2026-02-16T00:10:00Z",
                                    "completed_at": "2026-02-16T00:12:00Z",
                                    "steps": [
                                        {
                                            "number": 1,
                                            "name": "post processing",
                                            "status": "completed",
                                            "conclusion": "success",
                                            "started_at": "2026-02-16T00:10:00Z",
                                            "completed_at": "2026-02-16T00:12:00Z"
                                        }
                                    ]
                                }
                            ]
                        }
                        """);

        File.WriteAllText(metadataDirectory / "artifacts.json", """
                        {
                            "artifacts": [
                                {
                                    "id": 5001,
                                    "name": "build-tests-artifacts",
                                    "size_in_bytes": 1024,
                                    "created_at": "2026-02-16T00:10:30Z",
                                    "updated_at": "2026-02-16T00:10:30Z"
                                }
                            ]
                        }
                        """);

        File.WriteAllText(logsDirectory / "1001.log", """
                        2026-02-16T00:01:00.0000000Z ##[group]Run actions/upload-artifact@v6
                        2026-02-16T00:01:00.0000100Z with:
                        2026-02-16T00:01:00.0000200Z   name: build-tests-artifacts
                        2026-02-16T00:01:00.0000300Z   path: ./artifacts/test-results/**/*.trx
                        2026-02-16T00:01:00.0000400Z env:
                        2026-02-16T00:01:00.0000500Z   DOTNET_NOLOGO: true
                        2026-02-16T00:01:00.0000600Z ##[endgroup]
                        """);

        File.WriteAllText(logsDirectory / "1002.log", """
                        2026-02-16T00:11:00.0000000Z Processing build-tests-artifacts metadata
                        """);

        File.WriteAllText(artifactDirectory / "results.trx", """
                        <?xml version="1.0" encoding="utf-8"?>
                        <TestRun>
                          <Results>
                            <UnitTestResult testName="SampleTests.UploadedArtifactIsMapped"
                                            outcome="Passed"
                                            duration="00:00:01"
                                            startTime="2026-02-16T00:01:20.0000000Z"
                                            endTime="2026-02-16T00:01:21.0000000Z"
                                            machineName="runner-1" />
                          </Results>
                        </TestRun>
                        """);
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

    private sealed class EnvironmentVariableScope : IDisposable
    {
        private readonly Dictionary<string, string?> _previousValues = new(StringComparer.Ordinal);

        public EnvironmentVariableScope(IReadOnlyDictionary<string, string?> values)
        {
            foreach (var value in values)
            {
                _previousValues[value.Key] = Environment.GetEnvironmentVariable(value.Key);
                Environment.SetEnvironmentVariable(value.Key, value.Value);
            }
        }

        public void Dispose()
        {
            foreach (var previousValue in _previousValues)
            {
                Environment.SetEnvironmentVariable(previousValue.Key, previousValue.Value);
            }
        }
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
