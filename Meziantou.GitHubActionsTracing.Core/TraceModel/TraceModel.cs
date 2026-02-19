using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Meziantou.Framework;
using Microsoft.Build.Logging;
using Microsoft.Build.Logging.StructuredLogger;
using StructuredItem = Microsoft.Build.Logging.StructuredLogger.Item;
using StructuredProperty = Microsoft.Build.Logging.StructuredLogger.Property;
using StructuredProject = Microsoft.Build.Logging.StructuredLogger.Project;
using StructuredTarget = Microsoft.Build.Logging.StructuredLogger.Target;
using StructuredTaskParameterItem = Microsoft.Build.Logging.StructuredLogger.TaskParameterItem;
using StructuredTaskParameterProperty = Microsoft.Build.Logging.StructuredLogger.TaskParameterProperty;
using StructuredTask = Microsoft.Build.Logging.StructuredLogger.Task;

namespace Meziantou.GitHubActionsTracing;

internal sealed partial class TraceModel
{
    private static readonly JsonSerializerOptions JsonSerializerOptions = new(JsonSerializerDefaults.Web);
    private static readonly int[] Ansi256ColorSteps = [0, 95, 135, 175, 215, 255];
    private const int BinlogVersionWithDateTimeKind = 21; // Support DateTimeKind in DateTime properties

    private readonly List<TraceSpan> _spans = [];
    private readonly Dictionary<string, TraceSpan> _spansById = new(StringComparer.Ordinal);
    private readonly Dictionary<long, TraceSpan> _jobSpans = [];

    private TraceModel(WorkflowRunResponse workflowRun)
    {
        WorkflowRun = workflowRun;
    }

    public WorkflowRunResponse WorkflowRun { get; }

    public long WorkflowRunId => WorkflowRun.Id;

    public IReadOnlyList<TraceSpan> Spans => _spans;

    public TraceSpan RootSpan { get; private set; } = null!;

    public static TraceModel Load(FullPath path)
    {
        return Load(path, new TraceLoadOptions());
    }

    public static TraceModel Load(FullPath path, TraceLoadOptions options)
    {
        var run = ReadJson<WorkflowRunResponse>(path / "metadata" / "run.json");
        var jobsResponse = ReadJson<WorkflowJobsResponse>(path / "metadata" / "jobs.json");
        var artifactsResponse = ReadJson<WorkflowArtifactsResponse>(path / "metadata" / "artifacts.json");

        var model = new TraceModel(run);

        var workflowStart = run.RunStartedAt ?? run.CreatedAt ?? DateTimeOffset.UtcNow;
        var workflowEnd = run.UpdatedAt ?? workflowStart.AddSeconds(1);

        model.RootSpan = model.AddSpan(
            name: run.Name ?? run.DisplayTitle ?? $"Workflow run {run.Id}",
            kind: "workflow",
            startTime: workflowStart,
            endTime: workflowEnd,
            parentId: null,
            jobId: null,
            attributes: new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["workflow.run_id"] = run.Id,
                ["workflow.event"] = run.Event,
                ["workflow.head_branch"] = run.HeadBranch,
                ["workflow.head_sha"] = run.HeadSha,
                ["workflow.run_number"] = run.RunNumber,
                ["workflow.run_attempt"] = run.RunAttempt,
                ["workflow.repository"] = run.Repository?.FullName,
                ["workflow.url"] = run.HtmlUrl,
            });

        var jobs = (jobsResponse.Jobs ?? [])
            .OrderBy(static j => j.StartedAt ?? DateTimeOffset.MaxValue)
            .ThenBy(static j => j.Id)
            .ToList();

        var stepsByJob = new Dictionary<long, List<TraceSpan>>();

        foreach (var job in jobs)
        {
            var jobStart = job.StartedAt ?? job.CreatedAt ?? workflowStart;
            var jobEnd = job.CompletedAt ?? jobStart.AddSeconds(1);

            var queueTime = (job.StartedAt is not null && job.CreatedAt is not null)
                ? job.StartedAt.Value - job.CreatedAt.Value
                : TimeSpan.Zero;

            var jobSpan = model.AddSpan(
                name: job.Name ?? $"Job {job.Id}",
                kind: "job",
                startTime: jobStart,
                endTime: jobEnd,
                parentId: model.RootSpan.Id,
                jobId: job.Id,
                attributes: new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["job.id"] = job.Id,
                    ["job.status"] = job.Status,
                    ["job.conclusion"] = job.Conclusion,
                    ["job.queue_time_ms"] = queueTime.TotalMilliseconds,
                    ["job.runner.name"] = job.RunnerName,
                    ["job.runner.group"] = job.RunnerGroupName,
                    ["job.runner.labels"] = job.Labels is { Count: > 0 } ? string.Join(',', job.Labels) : null,
                });

            model._jobSpans[job.Id] = jobSpan;

            var stepSpans = new List<TraceSpan>();
            foreach (var step in job.Steps ?? [])
            {
                if (step.StartedAt is null || step.CompletedAt is null)
                    continue;

                var stepSpan = model.AddSpan(
                    name: step.Name ?? $"Step {step.Number}",
                    kind: "step",
                    startTime: step.StartedAt.Value,
                    endTime: step.CompletedAt.Value,
                    parentId: jobSpan.Id,
                    jobId: job.Id,
                    attributes: new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["step.number"] = step.Number,
                        ["step.status"] = step.Status,
                        ["step.conclusion"] = step.Conclusion,
                    });

                stepSpans.Add(stepSpan);
            }

            stepsByJob[job.Id] = stepSpans;
        }

        var logsByJob = new Dictionary<long, string>();
        foreach (var job in jobs)
        {
            var logPath = path / "logs" / "jobs" / $"{job.Id}.log";
            if (!File.Exists(logPath))
                continue;

            var content = File.ReadAllText(logPath);
            logsByJob[job.Id] = content;

            model.ParseJobLog(
                jobId: job.Id,
                logContent: content,
                stepSpans: stepsByJob.TryGetValue(job.Id, out var value) ? value : []);
        }

        // Jobs and steps from the GitHub API have 1-second precision, while logs contains the full datetime.
        // So, some spans may be outside of their parents (up to 1 second).
        model.RecomputeJobAndStepDurationsFromChildren();

        var artifactContexts = BuildArtifactContexts(path, artifactsResponse.Artifacts ?? []);
        MapArtifactsToJobs(artifactContexts, jobs, logsByJob);

        if (options.IncludeBinlog)
        {
            foreach (var artifact in artifactContexts)
            {
                if (artifact.MappedJobId is null)
                    continue;

                model.AddBinlogSpans(artifact, options.MinimumBinlogDuration);
            }
        }

        if (options.IncludeTests)
        {
            foreach (var artifact in artifactContexts)
            {
                if (artifact.MappedJobId is null)
                    continue;

                model.AddTestSpans(artifact, options.MinimumTestDuration);
            }
        }

        model.RecomputeJobAndStepDurationsFromChildren();

        return model;
    }

    public TraceSpan AddSpan(
        string name,
        string kind,
        DateTimeOffset startTime,
        DateTimeOffset endTime,
        string? parentId,
        long? jobId,
        Dictionary<string, object?>? attributes = null)
    {
        if (endTime < startTime)
            endTime = startTime;

        var span = new TraceSpan
        {
            Id = Guid.NewGuid().ToString("N"),
            ParentId = parentId,
            JobId = jobId,
            Name = name,
            Kind = kind,
            StartTime = startTime,
            EndTime = endTime,
            Attributes = attributes ?? new Dictionary<string, object?>(StringComparer.Ordinal),
        };

        _spans.Add(span);
        _spansById[span.Id] = span;

        return span;
    }

    public TraceSpan FindClosestParent(long jobId, DateTimeOffset startTime, DateTimeOffset endTime, string? preferredParentId = null)
    {
        if (endTime < startTime)
            endTime = startTime;

        if (!_jobSpans.TryGetValue(jobId, out var fallbackJobSpan))
            return RootSpan;

        var candidates = _spans
            .Where(span =>
                span.JobId == jobId &&
                span.StartTime <= startTime &&
                span.EndTime >= endTime)
            .ToList();

        if (preferredParentId is not null)
        {
            var preferredSpan = candidates.FirstOrDefault(span => span.Id == preferredParentId);
            if (preferredSpan is not null)
                return preferredSpan;
        }

        if (candidates.Count is 0)
            return fallbackJobSpan;

        return candidates
            .OrderBy(span => span.Duration)
            .ThenByDescending(GetDepth)
            .First();
    }

    private int GetDepth(TraceSpan span)
    {
        var depth = 0;
        var currentSpan = span;

        while (currentSpan.ParentId is not null && _spansById.TryGetValue(currentSpan.ParentId, out var parentSpan))
        {
            depth++;
            currentSpan = parentSpan;
            if (depth > 1024)
                break;
        }

        return depth;
    }

    private void RecomputeJobAndStepDurationsFromChildren()
    {
        var childrenByParentId = _spans
            .Where(static span => span.ParentId is not null)
            .GroupBy(span => span.ParentId!, StringComparer.Ordinal)
            .ToDictionary(static group => group.Key, static group => group.ToList(), StringComparer.Ordinal);

        ExpandJobAndStepDurationsFromChildren(childrenByParentId, adjustStepStartTime: true);
        AdjustFollowingStepStartTimes();
        ExpandJobAndStepDurationsFromChildren(childrenByParentId, adjustStepStartTime: false);
        AdjustFollowingStepStartTimes();
    }

    private void ExpandJobAndStepDurationsFromChildren(Dictionary<string, List<TraceSpan>> childrenByParentId, bool adjustStepStartTime)
    {
        var spansToRecompute = _spans
            .Where(static span => span.Kind is "job" or "step")
            .OrderByDescending(GetDepth)
            .ToList();

        foreach (var span in spansToRecompute)
        {
            if (!childrenByParentId.TryGetValue(span.Id, out var children))
                continue;

            var minStart = span.StartTime;
            var maxEnd = span.EndTime;
            foreach (var child in children)
            {
                if ((adjustStepStartTime || span.Kind == "job") && child.StartTime < minStart)
                    minStart = child.StartTime;

                if (child.EndTime > maxEnd)
                    maxEnd = child.EndTime;
            }

            span.StartTime = minStart;
            span.EndTime = maxEnd;
        }
    }

    private void AdjustFollowingStepStartTimes()
    {
        var stepsByJob = _spans
            .Where(static span => span.Kind == "step" && span.JobId is not null)
            .GroupBy(span => span.JobId!.Value)
            .ToList();

        foreach (var stepGroup in stepsByJob)
        {
            var orderedSteps = stepGroup
                .OrderBy(GetStepNumber)
                .ThenBy(static span => span.StartTime)
                .ToList();

            DateTimeOffset? previousEndTime = null;
            foreach (var step in orderedSteps)
            {
                if (previousEndTime is not null && step.StartTime < previousEndTime.Value)
                {
                    step.StartTime = previousEndTime.Value;
                }

                if (step.EndTime < step.StartTime)
                    step.EndTime = step.StartTime;

                previousEndTime = step.EndTime;
            }
        }
    }

    private static int GetStepNumber(TraceSpan span)
    {
        if (span.Attributes.TryGetValue("step.number", out var value) && value is int stepNumber)
            return stepNumber;

        return int.MaxValue;
    }

    private void ParseJobLog(long jobId, string logContent, List<TraceSpan> stepSpans)
    {
        if (!_jobSpans.TryGetValue(jobId, out var jobSpan))
            return;

        var groups = new Stack<TraceSpan>();
        DateTimeOffset? currentTimestamp = null;

        foreach (var rawLine in EnumerateLines(logContent))
        {
            var (timestamp, message) = ParseLogLine(rawLine);
            if (timestamp is not null)
                currentTimestamp = timestamp;

            var activeTimestamp = currentTimestamp;
            var parentSpan = groups.Count > 0
                ? groups.Peek()
                : GetStepSpan(stepSpans, activeTimestamp) ?? jobSpan;

            if (TryGetGroupStartTitle(message, out var groupTitle))
            {
                var start = activeTimestamp ?? parentSpan.StartTime;
                var groupSpan = AddSpan(
                    name: string.IsNullOrWhiteSpace(groupTitle) ? "group" : groupTitle,
                    kind: "log.group",
                    startTime: start,
                    endTime: start.AddMilliseconds(1),
                    parentId: parentSpan.Id,
                    jobId: jobId,
                    attributes: new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["log.group"] = true,
                    });

                groups.Push(groupSpan);
                continue;
            }

            if (IsGroupEnd(message))
            {
                if (groups.TryPop(out var groupSpan))
                {
                    var end = activeTimestamp ?? groupSpan.StartTime.AddMilliseconds(1);
                    groupSpan.EndTime = end < groupSpan.StartTime ? groupSpan.StartTime : end;
                }

                continue;
            }

            var annotation = ParseAnnotation(message);
            if (annotation is null)
                continue;

            parentSpan.Events.Add(new TraceEvent
            {
                Name = annotation.Value.Level,
                Timestamp = activeTimestamp ?? parentSpan.StartTime,
                Attributes = annotation.Value.Attributes,
                Message = annotation.Value.Message,
            });
        }

        while (groups.TryPop(out var groupSpan))
        {
            if (groupSpan.EndTime < groupSpan.StartTime)
                groupSpan.EndTime = groupSpan.StartTime;

            if (groupSpan.EndTime < jobSpan.EndTime)
                groupSpan.EndTime = jobSpan.EndTime;
        }
    }

    private void AddBinlogSpans(ArtifactContext artifact, TimeSpan minimumBinlogDuration)
    {
        var jobId = artifact.MappedJobId;
        if (jobId is null)
            return;

        foreach (var file in artifact.Files.Where(static f => f.Extension.Equals(".binlog", StringComparison.OrdinalIgnoreCase)))
        {
            if (!TryGetBinlogFileFormatVersion(file, out var binlogVersion))
            {
                AppLog.Warning($"Cannot determine binlog file format version for '{file}', skipping file");
                continue;
            }

            if (binlogVersion < BinlogVersionWithDateTimeKind)
            {
                AppLog.Warning($"Binlog '{file}' uses file format version {binlogVersion}, which does not support DateTimeKind. This file is skipped.");
                continue;
            }

            AppLog.Info($"Parsing binlog: {file}");

            Build build;
            try
            {
                build = Serialization.Read(file);
            }
            catch (Exception ex)
            {
                AppLog.Warning($"Cannot read binlog '{file}': {ex.Message}");
                continue;
            }

            var projectSpans = new Dictionary<StructuredProject, TraceSpan>();
            foreach (var project in EnumerateNodes<StructuredProject>(build))
            {
                if (project.StartTime == default || project.EndTime == default)
                    continue;

                var projectStart = ToDateTimeOffset(project.StartTime);
                var projectEnd = ToDateTimeOffset(project.EndTime);
                if (projectEnd < projectStart)
                    projectEnd = projectStart;

                var parentSpan = FindClosestParent(jobId.Value, projectStart, projectEnd);
                var projectSpan = AddSpan(
                    name: BuildProjectSpanName(project),
                    kind: "msbuild.project",
                    startTime: projectStart,
                    endTime: projectEnd,
                    parentId: parentSpan.Id,
                    jobId: jobId,
                    attributes: new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["artifact.id"] = artifact.Artifact.Id,
                        ["artifact.name"] = artifact.Artifact.Name,
                        ["binlog.file"] = file.Value,
                        ["project.file"] = project.ProjectFile,
                        ["project.configuration"] = project.Configuration,
                        ["project.platform"] = project.Platform,
                        ["project.target_framework"] = project.TargetFramework,
                    });

                foreach (var property in artifact.Hints.CustomProperties)
                {
                    projectSpan.Attributes[$"binlog.property.{property.Key}"] = property.Value;
                }

                projectSpans[project] = projectSpan;
            }

            foreach (var target in EnumerateNodes<StructuredTarget>(build))
            {
                if (target.Name is null)
                    continue;

                if (target.Name.StartsWith('_'))
                    continue;

                if (string.Equals(target.Name, "CallTarget", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (target.Duration < minimumBinlogDuration)
                    continue;

                if (target.StartTime == default || target.EndTime == default)
                    continue;

                var targetStart = ToDateTimeOffset(target.StartTime);
                var targetEnd = ToDateTimeOffset(target.EndTime);
                var parentSpan = FindClosestParent(jobId.Value, targetStart, targetEnd);
                var parentId = parentSpan.Id;

                var project = GetProjectNode(target);
                if (project is not null && projectSpans.TryGetValue(project, out var projectSpan))
                {
                    parentId = projectSpan.Id;
                }

                var targetName = BuildMsBuildNodeName(target.Name, project);

                var targetSpan = AddSpan(
                    name: targetName,
                    kind: "msbuild.target",
                    startTime: targetStart,
                    endTime: targetEnd,
                    parentId: parentId,
                    jobId: jobId,
                    attributes: new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["artifact.id"] = artifact.Artifact.Id,
                        ["artifact.name"] = artifact.Artifact.Name,
                        ["binlog.file"] = file.Value,
                        ["project.file"] = project?.ProjectFile,
                        ["project.target_framework"] = project?.TargetFramework,
                        ["target.succeeded"] = target.Succeeded,
                    });

                foreach (var property in artifact.Hints.CustomProperties)
                {
                    targetSpan.Attributes[$"binlog.property.{property.Key}"] = property.Value;
                }

                foreach (var task in EnumerateNodes<StructuredTask>(target))
                {
                    if (string.Equals(task.Name, "CallTarget", StringComparison.OrdinalIgnoreCase))
                        continue;

                    if (task.StartTime == default || task.EndTime == default)
                        continue;

                    var taskStart = ToDateTimeOffset(task.StartTime);
                    var taskEnd = ToDateTimeOffset(task.EndTime);
                    if (taskEnd < taskStart)
                        taskEnd = taskStart;

                    var taskName = BuildMsBuildNodeName(task.Name ?? "Task", project);

                    var taskAttributes = new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["artifact.id"] = artifact.Artifact.Id,
                        ["artifact.name"] = artifact.Artifact.Name,
                        ["binlog.file"] = file.Value,
                        ["project.file"] = project?.ProjectFile,
                        ["project.target_framework"] = project?.TargetFramework,
                        ["task.from_assembly"] = task.FromAssembly,
                    };

                    AddTaskParametersAsAttributes(task, taskAttributes);

                    AddSpan(
                        name: taskName,
                        kind: "msbuild.task",
                        startTime: taskStart,
                        endTime: taskEnd,
                        parentId: targetSpan.Id,
                        jobId: jobId,
                        attributes: taskAttributes);
                }
            }
        }
    }

    private static void AddTaskParametersAsAttributes(StructuredTask task, Dictionary<string, object?> attributes)
    {
        var taskParameters = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);

        AddTaskParameterValue(taskParameters, "CommandLineArguments", task.CommandLineArguments);

        foreach (var parameter in EnumerateNodes<StructuredTaskParameterProperty>(task))
        {
            AddTaskParameterValue(taskParameters, parameter.ParameterName, parameter.Value);
        }

        foreach (var parameter in EnumerateNodes<StructuredTaskParameterItem>(task))
        {
            foreach (var item in EnumerateNodes<StructuredItem>(parameter))
            {
                AddTaskParameterValue(taskParameters, parameter.ParameterName, item.Text);
            }
        }

        foreach (var taskParameter in taskParameters)
        {
            attributes[$"task.parameter.{taskParameter.Key}"] = string.Join(';', taskParameter.Value);
        }
    }

    private static void AddTaskParameterValue(Dictionary<string, SortedSet<string>> taskParameters, string? parameterName, string? parameterValue)
    {
        if (string.IsNullOrWhiteSpace(parameterName))
            return;

        if (string.IsNullOrWhiteSpace(parameterValue))
            return;

        if (!taskParameters.TryGetValue(parameterName, out var values))
        {
            values = new SortedSet<string>(StringComparer.Ordinal);
            taskParameters.Add(parameterName, values);
        }

        values.Add(parameterValue);
    }

    private static StructuredProject? GetProjectNode(StructuredTarget target)
    {
        foreach (var parent in target.GetParentChainIncludingThis())
        {
            if (parent is StructuredProject project)
                return project;
        }

        return null;
    }

    private static string BuildProjectSpanName(StructuredProject project)
    {
        if (!string.IsNullOrWhiteSpace(project.Name))
            return project.Name;

        if (!string.IsNullOrWhiteSpace(project.ProjectFile))
            return Path.GetFileName(project.ProjectFile);

        return "Project";
    }

    private static string BuildMsBuildNodeName(string name, StructuredProject? project)
    {
        if (!string.Equals(name, "Csc", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(name, "Restore", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(name, "RestoreTask", StringComparison.OrdinalIgnoreCase))
        {
            return name;
        }

        var projectContext = GetProjectContext(project);
        if (string.IsNullOrWhiteSpace(projectContext))
            return name;

        return $"{name} ({projectContext})";
    }

    private static string? GetProjectContext(StructuredProject? project)
    {
        if (project is null)
            return null;

        if (!string.IsNullOrWhiteSpace(project.ProjectFile))
            return Path.GetFileName(project.ProjectFile);

        if (!string.IsNullOrWhiteSpace(project.Name))
            return project.Name;

        return null;
    }

    private static bool TryGetBinlogFileFormatVersion(FullPath file, out int fileFormatVersion)
    {
        fileFormatVersion = default;

        try
        {
            using var reader = BinaryLogReplayEventSource.OpenReader(file.Value);
            fileFormatVersion = reader.ReadInt32();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void AddTestSpans(ArtifactContext artifact, TimeSpan minimumTestDuration)
    {
        var jobId = artifact.MappedJobId;
        if (jobId is null)
            return;

        foreach (var file in artifact.Files)
        {
            if (file.Extension.Equals(".trx", StringComparison.OrdinalIgnoreCase))
            {
                ParseTrxFile(file, jobId.Value, minimumTestDuration, artifact);
                continue;
            }

            if (!file.Extension.Equals(".xml", StringComparison.OrdinalIgnoreCase))
                continue;

            if (LooksLikeJunit(file))
            {
                ParseJunitFile(file, jobId.Value, minimumTestDuration, artifact);
            }
        }
    }

    private void ParseTrxFile(FullPath file, long jobId, TimeSpan minimumTestDuration, ArtifactContext artifact)
    {
        XDocument document;
        try
        {
            document = XDocument.Load(file.Value);
        }
        catch
        {
            return;
        }

        var unitTestResults = document
            .Descendants()
            .Where(static element => element.Name.LocalName.Equals("UnitTestResult", StringComparison.OrdinalIgnoreCase));

        foreach (var result in unitTestResults)
        {
            var duration = ParseTimeSpan(result.Attribute("duration")?.Value) ?? TimeSpan.Zero;
            if (duration < minimumTestDuration)
                continue;

            var start = ParseDateTimeOffset(result.Attribute("startTime")?.Value);
            var end = ParseDateTimeOffset(result.Attribute("endTime")?.Value);

            if (start is null && end is null)
                continue;

            if (start is null && end is not null)
                start = end.Value - duration;

            if (end is null && start is not null)
                end = start.Value + duration;

            if (start is null || end is null)
                continue;

            var parentSpan = FindClosestParent(jobId, start.Value, end.Value);

            AddSpan(
                name: BuildTestSpanName(result.Attribute("testName")?.Value),
                kind: "test",
                startTime: start.Value,
                endTime: end.Value,
                parentId: parentSpan.Id,
                jobId: jobId,
                attributes: new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["artifact.id"] = artifact.Artifact.Id,
                    ["artifact.name"] = artifact.Artifact.Name,
                    ["test.file"] = file.Value,
                    ["test.framework"] = "trx",
                    ["test.outcome"] = result.Attribute("outcome")?.Value,
                    ["test.machine_name"] = result.Attribute("machineName")?.Value,
                });
        }
    }

    private void ParseJunitFile(FullPath file, long jobId, TimeSpan minimumTestDuration, ArtifactContext artifact)
    {
        XDocument document;
        try
        {
            document = XDocument.Load(file.Value);
        }
        catch
        {
            return;
        }

        var testSuites = document
            .Descendants()
            .Where(static element => element.Name.LocalName.Equals("testsuite", StringComparison.OrdinalIgnoreCase))
            .ToList();

        foreach (var suite in testSuites)
        {
            var suiteTimestamp = ParseDateTimeOffset(suite.Attribute("timestamp")?.Value);
            var cursor = suiteTimestamp ?? _jobSpans[jobId].StartTime;

            var testCases = suite
                .Descendants()
                .Where(static element => element.Name.LocalName.Equals("testcase", StringComparison.OrdinalIgnoreCase));

            foreach (var testCase in testCases)
            {
                var durationSeconds = double.TryParse(testCase.Attribute("time")?.Value, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var value) ? value : 0;
                var duration = TimeSpan.FromSeconds(durationSeconds);
                if (duration < minimumTestDuration)
                    continue;

                var start = suiteTimestamp ?? cursor;
                var end = start + duration;
                cursor = end;

                var testName = BuildJunitTestName(testCase);
                var status = testCase.Elements().Select(static child => child.Name.LocalName.ToLowerInvariant()).FirstOrDefault() switch
                {
                    "failure" => "failure",
                    "error" => "error",
                    "skipped" => "skipped",
                    _ => "passed",
                };

                var parentSpan = FindClosestParent(jobId, start, end);

                AddSpan(
                    name: BuildTestSpanName(testName),
                    kind: "test",
                    startTime: start,
                    endTime: end,
                    parentId: parentSpan.Id,
                    jobId: jobId,
                    attributes: new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["artifact.id"] = artifact.Artifact.Id,
                        ["artifact.name"] = artifact.Artifact.Name,
                        ["test.file"] = file.Value,
                        ["test.framework"] = "junit",
                        ["test.outcome"] = status,
                        ["test.hostname"] = suite.Attribute("hostname")?.Value,
                    });
            }
        }
    }

    private static string BuildJunitTestName(XElement testCase)
    {
        var className = testCase.Attribute("classname")?.Value;
        var testName = testCase.Attribute("name")?.Value ?? "Test";

        return string.IsNullOrWhiteSpace(className)
            ? testName
            : className + "." + testName;
    }

    private static string BuildTestSpanName(string? testName)
    {
        if (string.IsNullOrWhiteSpace(testName))
            return "Test";

        return testName.StartsWith("Test: ", StringComparison.Ordinal)
            ? testName
            : "Test: " + testName;
    }

    private static bool LooksLikeJunit(FullPath file)
    {
        try
        {
            using var stream = File.OpenRead(file);
            var document = XDocument.Load(stream);
            var root = document.Root?.Name.LocalName;
            return string.Equals(root, "testsuite", StringComparison.OrdinalIgnoreCase)
                   || string.Equals(root, "testsuites", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static List<ArtifactContext> BuildArtifactContexts(FullPath path, List<WorkflowRunArtifact> artifacts)
    {
        var artifactsRoot = path / "artifacts";
        var contexts = new List<ArtifactContext>();

        foreach (var artifact in artifacts)
        {
            var directory = Directory
                .EnumerateDirectories(artifactsRoot)
                .FirstOrDefault(candidate => Path.GetFileName(candidate).StartsWith($"{artifact.Id}-", StringComparison.OrdinalIgnoreCase));

            if (directory is null)
                continue;

            var filesDirectory = FullPath.FromPath(directory) / "files";
            if (!Directory.Exists(filesDirectory))
                continue;

            var files = Directory
                .EnumerateFiles(filesDirectory, "*", SearchOption.AllDirectories)
                .Select(FullPath.FromPath)
                .ToList();

            var hints = ExtractHints(files);

            contexts.Add(new ArtifactContext
            {
                Artifact = artifact,
                Directory = FullPath.FromPath(directory),
                FilesDirectory = filesDirectory,
                Files = files,
                Hints = hints,
            });
        }

        return contexts;
    }

    private static ArtifactHints ExtractHints(List<FullPath> files)
    {
        var hints = new ArtifactHints();

        foreach (var file in files)
        {
            if (file.Extension.Equals(".trx", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    var document = XDocument.Load(file.Value);
                    foreach (var result in document.Descendants().Where(static element => element.Name.LocalName.Equals("UnitTestResult", StringComparison.OrdinalIgnoreCase)))
                    {
                        var machineName = result.Attribute("machineName")?.Value;
                        if (!string.IsNullOrWhiteSpace(machineName))
                            hints.MachineNames.Add(machineName);
                    }
                }
                catch
                {
                    // ignored
                }
            }
            else if (file.Extension.Equals(".xml", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    var document = XDocument.Load(file.Value);
                    foreach (var suite in document.Descendants().Where(static element => element.Name.LocalName.Equals("testsuite", StringComparison.OrdinalIgnoreCase)))
                    {
                        var hostName = suite.Attribute("hostname")?.Value;
                        if (!string.IsNullOrWhiteSpace(hostName))
                            hints.MachineNames.Add(hostName);
                    }
                }
                catch
                {
                    // ignored
                }
            }
        }

        foreach (var binlogFile in files.Where(static file => file.Extension.Equals(".binlog", StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                var build = Serialization.Read(binlogFile.Value);
                foreach (var property in EnumerateNodes<StructuredProperty>(build))
                {
                    if (property.Name is null || property.Value is null)
                        continue;

                    if (!property.Name.StartsWith("_GitHub", StringComparison.OrdinalIgnoreCase)
                        && !property.Name.StartsWith("_Runner", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    hints.CustomProperties[property.Name] = property.Value;
                }

                if (hints.CustomProperties.TryGetValue("_GitHubJobId", out var gitHubJobId))
                    hints.GitHubJobId = gitHubJobId;

                if (hints.CustomProperties.TryGetValue("_RunnerName", out var runnerName))
                    hints.RunnerName = runnerName;

                break;
            }
            catch
            {
                // ignored
            }
        }

        return hints;
    }

    private static void MapArtifactsToJobs(List<ArtifactContext> artifacts, List<WorkflowRunJob> jobs, Dictionary<long, string> logsByJob)
    {
        var uploadArtifactNamesByJob = logsByJob
            .ToDictionary(static entry => entry.Key, static entry => ExtractUploadArtifactNames(entry.Value));

        foreach (var artifact in artifacts)
        {
            long? bestJobId = null;
            var bestScore = int.MinValue;

            foreach (var job in jobs)
            {
                var score = 0;

                if (uploadArtifactNamesByJob.TryGetValue(job.Id, out var uploadedArtifactNames)
                    && uploadedArtifactNames.Contains(artifact.Artifact.Name))
                {
                    score += 30;
                }
                else if (logsByJob.TryGetValue(job.Id, out var jobLog)
                         && jobLog.Contains(artifact.Artifact.Name, StringComparison.OrdinalIgnoreCase))
                {
                    score += 6;
                }

                if (artifact.Artifact.CreatedAt is not null && job.StartedAt is not null && job.CompletedAt is not null)
                {
                    var start = job.StartedAt.Value.AddMinutes(-2);
                    var end = job.CompletedAt.Value.AddMinutes(2);
                    if (artifact.Artifact.CreatedAt.Value >= start && artifact.Artifact.CreatedAt.Value <= end)
                        score += 3;
                }

                if (!string.IsNullOrWhiteSpace(job.RunnerName) && artifact.Hints.MachineNames.Contains(job.RunnerName))
                    score += 6;

                if (!string.IsNullOrWhiteSpace(artifact.Hints.RunnerName)
                    && !string.IsNullOrWhiteSpace(job.RunnerName)
                    && string.Equals(artifact.Hints.RunnerName, job.RunnerName, StringComparison.OrdinalIgnoreCase))
                {
                    score += 8;
                }

                if (!string.IsNullOrWhiteSpace(artifact.Hints.GitHubJobId)
                    && !string.IsNullOrWhiteSpace(job.Name)
                    && string.Equals(artifact.Hints.GitHubJobId, job.Name, StringComparison.OrdinalIgnoreCase))
                {
                    score += 12;
                }

                if (score > bestScore)
                {
                    bestScore = score;
                    bestJobId = job.Id;
                }
            }

            if (bestJobId is null && jobs.Count is 1)
                bestJobId = jobs[0].Id;

            artifact.MappedJobId = bestScore > 0 ? bestJobId : null;
            if (artifact.MappedJobId is not null)
            {
                AppLog.Info($"Mapped artifact {artifact.Artifact.Id} ({artifact.Artifact.Name}) to job {artifact.MappedJobId}");
            }
            else
            {
                AppLog.Warning($"Cannot map artifact {artifact.Artifact.Id} ({artifact.Artifact.Name}) to a job");
            }
        }
    }

    private static HashSet<string> ExtractUploadArtifactNames(string jobLog)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var groups = new Stack<bool>();
        var uploadArtifactGroupDepth = 0;
        var inUploadArtifactWithSection = false;

        foreach (var line in EnumerateLines(jobLog))
        {
            var (_, message) = ParseLogLine(line);
            if (TryGetGroupStartTitle(message, out var title))
            {
                var isUploadArtifactGroup = title.Contains("actions/upload-artifact", StringComparison.OrdinalIgnoreCase);
                groups.Push(isUploadArtifactGroup);
                if (isUploadArtifactGroup)
                {
                    uploadArtifactGroupDepth++;
                    inUploadArtifactWithSection = false;
                }

                continue;
            }

            if (IsGroupEnd(message))
            {
                if (groups.TryPop(out var endedUploadArtifactGroup) && endedUploadArtifactGroup)
                {
                    uploadArtifactGroupDepth--;
                    inUploadArtifactWithSection = false;
                }

                continue;
            }

            if (uploadArtifactGroupDepth <= 0)
                continue;

            var trimmedMessage = message.Trim();
            if (trimmedMessage.Equals("with:", StringComparison.OrdinalIgnoreCase))
            {
                inUploadArtifactWithSection = true;
                continue;
            }

            if (trimmedMessage.Equals("env:", StringComparison.OrdinalIgnoreCase))
            {
                inUploadArtifactWithSection = false;
                continue;
            }

            if (!inUploadArtifactWithSection)
                continue;

            if (TryGetUploadArtifactName(trimmedMessage, out var artifactName))
                result.Add(artifactName);
        }

        return result;
    }

    private static bool TryGetUploadArtifactName(string message, out string artifactName)
    {
        artifactName = string.Empty;

        var separatorIndex = message.IndexOf(':', StringComparison.Ordinal);
        if (separatorIndex <= 0)
            return false;

        var key = message[..separatorIndex].Trim();
        if (!key.Equals("name", StringComparison.OrdinalIgnoreCase))
            return false;

        var value = message[(separatorIndex + 1)..].Trim();
        if (string.IsNullOrWhiteSpace(value))
            return false;

        artifactName = value;
        return true;
    }

    private static TraceSpan? GetStepSpan(List<TraceSpan> stepSpans, DateTimeOffset? timestamp)
    {
        if (timestamp is null)
            return null;

        return stepSpans
            .Where(stepSpan => stepSpan.StartTime <= timestamp.Value && stepSpan.EndTime >= timestamp.Value)
            .OrderBy(stepSpan => stepSpan.Duration)
            .FirstOrDefault();
    }

    private static (DateTimeOffset? Timestamp, string Message) ParseLogLine(string line)
    {
        var match = LogTimestampRegex.Match(line);
        if (!match.Success)
            return (null, line);

        if (!DateTimeOffset.TryParse(match.Groups["ts"].Value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var timestamp))
            return (null, line);

        return (timestamp, match.Groups["msg"].Value);
    }

    private static bool TryGetGroupStartTitle(string message, out string title)
    {
        if (message.StartsWith("::group::", StringComparison.Ordinal))
        {
            title = message[9..].Trim();
            return true;
        }

        if (message.StartsWith("##[group]", StringComparison.Ordinal))
        {
            title = message[9..].Trim();
            return true;
        }

        title = string.Empty;
        return false;
    }

    private static bool IsGroupEnd(string message)
    {
        return message.StartsWith("::endgroup::", StringComparison.Ordinal)
            || message.StartsWith("##[endgroup]", StringComparison.Ordinal);
    }

    private static (string Level, string Message, Dictionary<string, object?> Attributes)? ParseAnnotation(string message)
    {
        var messageWithoutAnsi = StripAnsiEscapeSequences(message);

        var commandAnnotation = ParseWorkflowCommandAnnotation(messageWithoutAnsi);
        if (commandAnnotation is not null)
            return commandAnnotation;

        var legacyAnnotation = ParseLegacyAnnotation(messageWithoutAnsi);
        if (legacyAnnotation is not null)
            return legacyAnnotation;

        if (string.IsNullOrWhiteSpace(messageWithoutAnsi))
            return null;

        var ansiLevel = GetLevelFromAnsiColor(message);
        if (ansiLevel is null)
            return null;

        return (
            Level: ansiLevel,
            Message: messageWithoutAnsi,
            Attributes: new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["annotation.source"] = "ansi",
            });
    }

    private static (string Level, string Message, Dictionary<string, object?> Attributes)? ParseWorkflowCommandAnnotation(string message)
    {
        var match = AnnotationRegex.Match(message);
        if (!match.Success)
            return null;

        var attributes = new Dictionary<string, object?>(StringComparer.Ordinal);
        var props = match.Groups["props"].Value;
        if (!string.IsNullOrWhiteSpace(props))
        {
            var trimmed = props.Trim();
            foreach (var segment in trimmed.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var separatorIndex = segment.IndexOf('=', StringComparison.Ordinal);
                if (separatorIndex <= 0)
                    continue;

                var key = segment[..separatorIndex].Trim();
                var value = segment[(separatorIndex + 1)..].Trim();
                if (!string.IsNullOrEmpty(key))
                    attributes[key] = value;
            }
        }

        return (
            Level: match.Groups["level"].Value.ToLowerInvariant(),
            Message: match.Groups["message"].Value,
            Attributes: attributes);
    }

    private static (string Level, string Message, Dictionary<string, object?> Attributes)? ParseLegacyAnnotation(string message)
    {
        var match = LegacyAnnotationRegex.Match(message);
        if (!match.Success)
            return null;

        return (
            Level: match.Groups["level"].Value.ToLowerInvariant(),
            Message: match.Groups["message"].Value,
            Attributes: new Dictionary<string, object?>(StringComparer.Ordinal));
    }

    private static string? GetLevelFromAnsiColor(string message)
    {
        var severity = 0;
        var matches = AnsiSgrRegex.Matches(message);
        foreach (Match match in matches)
        {
            var codesValue = match.Groups["codes"].Value;
            if (string.IsNullOrWhiteSpace(codesValue))
                continue;

            var codes = codesValue.Split(';', StringSplitOptions.TrimEntries);
            for (var i = 0; i < codes.Length; i++)
            {
                if (!int.TryParse(codes[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out var code))
                    continue;

                switch (code)
                {
                    case 31:
                    case 91:
                        severity = Math.Max(severity, 2);
                        break;

                    case 33:
                    case 93:
                        severity = Math.Max(severity, 1);
                        break;

                    case 38 when i + 2 < codes.Length && int.TryParse(codes[i + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var mode):
                        if (mode is 5 && int.TryParse(codes[i + 2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var colorIndex))
                        {
                            severity = Math.Max(severity, GetSeverityFromAnsi256Color(colorIndex));
                            i += 2;
                        }
                        else if (mode is 2
                                 && i + 4 < codes.Length
                                 && int.TryParse(codes[i + 2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var red)
                                 && int.TryParse(codes[i + 3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var green)
                                 && int.TryParse(codes[i + 4], NumberStyles.Integer, CultureInfo.InvariantCulture, out var blue))
                        {
                            severity = Math.Max(severity, GetSeverityFromRgbColor(red, green, blue));
                            i += 4;
                        }

                        break;
                }

                if (severity is 2)
                    return "error";
            }
        }

        return severity switch
        {
            2 => "error",
            1 => "warning",
            _ => null,
        };
    }

    private static int GetSeverityFromAnsi256Color(int colorIndex)
    {
        if (colorIndex is 1 or 9)
            return 2;

        if (colorIndex is 3 or 11)
            return 1;

        if (colorIndex is < 16 or > 231)
            return 0;

        var normalized = colorIndex - 16;
        var blue = Ansi256ColorSteps[normalized % 6];
        var green = Ansi256ColorSteps[(normalized / 6) % 6];
        var red = Ansi256ColorSteps[(normalized / 36) % 6];
        return GetSeverityFromRgbColor(red, green, blue);
    }

    private static int GetSeverityFromRgbColor(int red, int green, int blue)
    {
        if (red >= 160 && green <= 120 && blue <= 120)
            return 2;

        if (red >= 160 && green >= 120 && blue <= 120)
            return 1;

        return 0;
    }

    private static string StripAnsiEscapeSequences(string value)
    {
        return AnsiEscapeRegex.Replace(value, string.Empty);
    }

    private static IEnumerable<string> EnumerateLines(string text)
    {
        using var reader = new StringReader(text);
        while (reader.ReadLine() is { } line)
            yield return line;
    }

    private static T ReadJson<T>(FullPath path)
    {
        using var stream = File.OpenRead(path);
        return JsonSerializer.Deserialize<T>(stream, JsonSerializerOptions)
               ?? throw new InvalidOperationException($"Cannot deserialize JSON file '{path}'");
    }

    private static IEnumerable<TNode> EnumerateNodes<TNode>(BaseNode root)
        where TNode : BaseNode
    {
        if (root is TNode typedNode)
            yield return typedNode;

        if (root is not TreeNode treeNode)
            yield break;

        foreach (var child in treeNode.Children)
        {
            foreach (var nested in EnumerateNodes<TNode>(child))
                yield return nested;
        }
    }

    private static DateTimeOffset ToDateTimeOffset(DateTime dateTime)
    {
        if (dateTime.Kind is DateTimeKind.Utc)
            return new DateTimeOffset(dateTime, TimeSpan.Zero);

        return new DateTimeOffset(DateTime.SpecifyKind(dateTime, DateTimeKind.Utc), TimeSpan.Zero);
    }

    private static DateTimeOffset? ParseDateTimeOffset(string? value)
    {
        if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed))
            return parsed;

        return null;
    }

    private static TimeSpan? ParseTimeSpan(string? value)
    {
        if (TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out var parsed))
            return parsed;

        return null;
    }

    [GeneratedRegex(@"^::(?<level>notice|warning|error)(?<props>[^:]*)::(?<message>.*)$", RegexOptions.IgnoreCase)]
    private static partial Regex AnnotationRegex { get; }

    [GeneratedRegex(@"^\s*##\[(?<level>notice|warning|error)\](?<message>.*)$", RegexOptions.IgnoreCase)]
    private static partial Regex LegacyAnnotationRegex { get; }

    [GeneratedRegex(@"\x1B\[(?<codes>[0-9;]*)m")]
    private static partial Regex AnsiSgrRegex { get; }

    [GeneratedRegex(@"\x1B\[[0-?]*[ -/]*[@-~]")]
    private static partial Regex AnsiEscapeRegex { get; }

    [GeneratedRegex(@"^(?<ts>\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d+)?Z)\s(?<msg>.*)$")]
    private static partial Regex LogTimestampRegex { get; }
}
