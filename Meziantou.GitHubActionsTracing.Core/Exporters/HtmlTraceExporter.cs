using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Meziantou.Framework;

namespace Meziantou.GitHubActionsTracing.Exporters;

internal sealed class HtmlTraceExporter : ITraceExporter
{
    private readonly FullPath _outputPath;

    public HtmlTraceExporter(FullPath outputPath)
    {
        _outputPath = outputPath;
    }

    public async Task ExportAsync(TraceModel model)
    {
        AppLog.Info($"Writing HTML trace: {_outputPath}");
        await ExportToFileAsync(model, _outputPath);
    }

    private static async Task ExportToFileAsync(TraceModel model, FullPath outputPath)
    {
        outputPath.CreateParentDirectory();

        var html = BuildHtmlContent(model);
        await File.WriteAllTextAsync(outputPath, html);
    }

    private static string BuildHtmlContent(TraceModel model)
    {
        var spans = model.Spans
            .OrderBy(static span => span.StartTime)
            .ToList();

        var jobs = spans
            .Where(static span => span.Kind == "job")
            .OrderBy(static span => span.StartTime)
            .ToList();

        var minTime = spans.Min(static span => span.StartTime);
        var maxTime = spans.Max(static span => span.EndTime);
        var totalDuration = (maxTime - minTime).TotalMilliseconds;
        var idleDuration = ComputeIdleDurationMilliseconds(spans, minTime, maxTime);
        var idleDurationSeconds = idleDuration / 1000;
        var idlePercentage = totalDuration > 0 ? idleDuration / totalDuration : 0;

        var spansData = BuildSpansData(spans, minTime, jobs);
        var jobsData = BuildJobsData(jobs);
        var workflowRunUrl = model.WorkflowRun.HtmlUrl;

        var html = new StringBuilder();
        html.AppendLine("<!DOCTYPE html>");
        html.AppendLine("<html lang=\"en\">");
        html.AppendLine("<head>");
        html.AppendLine("    <meta charset=\"UTF-8\">");
        html.AppendLine("    <meta name=\"viewport\" content=\"width=device-width, initial-scale=1.0\">");
        html.AppendLine($"    <title>{HtmlEncode(model.WorkflowRun.Name ?? "Workflow Run")} - Trace Viewer</title>");
        html.AppendLine("    <style>");
        html.AppendLine(GetCss());
        html.AppendLine("    </style>");
        html.AppendLine("</head>");
        html.AppendLine("<body>");
        html.AppendLine("    <div id=\"header\" class=\"header\">");
        html.AppendLine($"        <h1>{HtmlEncode(model.WorkflowRun.Name ?? "Workflow Run")}</h1>");
        html.AppendLine(CultureInfo.InvariantCulture, $"        <div class=\"info\">Duration: {FormatDuration((maxTime - minTime).TotalMilliseconds)} | Idle: {idleDurationSeconds:F1}s ({idlePercentage:P1}) | Spans: {spans.Count} | Jobs: {jobs.Count}</div>");
        if (!string.IsNullOrWhiteSpace(workflowRunUrl))
        {
            html.AppendLine($"        <div class=\"info\"><a class=\"run-link\" href=\"{HtmlEncode(workflowRunUrl)}\" target=\"_blank\" rel=\"noopener noreferrer\">View run on GitHub ↗</a></div>");
        }

        html.AppendLine("        <div class=\"header-controls\">");
        html.AppendLine("            <label for=\"search\">Search:</label>");
        html.AppendLine("            <input id=\"search\" class=\"search-input\" type=\"text\" placeholder=\"Span name contains...\" autocomplete=\"off\" />");
        html.AppendLine("            <span id=\"search-status\" class=\"search-status\"></span>");
        html.AppendLine("            <span class=\"filters-separator\" aria-hidden=\"true\">|</span>");
        html.AppendLine("            <label class=\"filter-option\" for=\"filter-msbuild-targets\"><input id=\"filter-msbuild-targets\" type=\"checkbox\" checked /> Show MSBuild Targets</label>");
        html.AppendLine("            <label class=\"filter-option\" for=\"filter-msbuild-tasks\"><input id=\"filter-msbuild-tasks\" type=\"checkbox\" checked /> Show MSBuild Tasks</label>");
        html.AppendLine("            <label class=\"filter-option\" for=\"filter-tests\"><input id=\"filter-tests\" type=\"checkbox\" checked /> Show Tests</label>");
        html.AppendLine("        </div>");
        html.AppendLine("    </div>");
        html.AppendLine("    <div id=\"container\">");
        html.AppendLine("        <canvas id=\"canvas\"></canvas>");
        html.AppendLine("    </div>");
        html.AppendLine("    <div id=\"tooltip\" class=\"tooltip\"></div>");
        html.AppendLine("    <script>");
        html.AppendLine($"        const spansData = {spansData};");
        html.AppendLine($"        const jobsData = {jobsData};");
        html.AppendLine(CultureInfo.InvariantCulture, $"        const minTime = {minTime.ToUnixTimeMilliseconds()};");
        html.AppendLine(CultureInfo.InvariantCulture, $"        const totalDuration = {totalDuration.ToString(CultureInfo.InvariantCulture)};");
        html.AppendLine(GetJavaScript());
        html.AppendLine("    </script>");
        html.AppendLine("</body>");
        html.AppendLine("</html>");

        return html.ToString();
    }

    private static string BuildSpansData(List<TraceSpan> spans, DateTimeOffset minTime, List<TraceSpan> jobs)
    {
        var jobIndexMap = jobs
            .Select((job, index) => new { JobId = job.JobId ?? 0, Index = index })
            .Where(x => x.JobId != 0)
            .ToDictionary(x => x.JobId, x => x.Index);

        var spansJson = spans.Select(span =>
        {
            var jobIndex = span.JobId.HasValue && jobIndexMap.TryGetValue(span.JobId.Value, out var idx)
                ? idx
                : -1;

            return new
            {
                id = span.Id,
                name = span.Name,
                kind = span.Kind,
                start = (span.StartTime - minTime).TotalMilliseconds,
                duration = span.Duration.TotalMilliseconds,
                jobIndex,
                parentId = span.ParentId,
                attributes = span.Attributes.ToDictionary(
                    kvp => kvp.Key,
                    kvp => kvp.Value?.ToString() ?? "",
                    StringComparer.Ordinal
                ),
            };
        }).ToList();

        return JsonSerializer.Serialize(spansJson, new JsonSerializerOptions
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            WriteIndented = false,
        });
    }

    private static string BuildJobsData(List<TraceSpan> jobs)
    {
        var jobsJson = jobs.Select(job => new
        {
            id = job.JobId ?? 0,
            name = job.Name,
        }).ToList();

        return JsonSerializer.Serialize(jobsJson, new JsonSerializerOptions
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            WriteIndented = false,
        });
    }

    private static string HtmlEncode(string text)
    {
        return text
            .Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal)
            .Replace("\"", "&quot;", StringComparison.Ordinal)
            .Replace("'", "&#39;", StringComparison.Ordinal);
    }

    private static string FormatDuration(double milliseconds)
    {
        var ts = TimeSpan.FromMilliseconds(milliseconds);
        if (ts.TotalHours >= 1)
            return $"{ts.Hours}h {ts.Minutes}m {ts.Seconds}s";
        if (ts.TotalMinutes >= 1)
            return $"{ts.Minutes}m {ts.Seconds}s";
        if (ts.TotalSeconds >= 1)
            return $"{ts.Seconds}.{ts.Milliseconds:000}s";
        return $"{ts.TotalMilliseconds:F0}ms";
    }

    private static double ComputeIdleDurationMilliseconds(List<TraceSpan> spans, DateTimeOffset minTime, DateTimeOffset maxTime)
    {
        if (maxTime <= minTime)
            return 0;

        var activeIntervals = spans
            .Where(static span => !string.Equals(span.Kind, "workflow", StringComparison.Ordinal))
            .Select(span => (
                Start: span.StartTime < minTime ? minTime : span.StartTime,
                End: span.EndTime > maxTime ? maxTime : span.EndTime))
            .Where(static interval => interval.End > interval.Start)
            .OrderBy(static interval => interval.Start)
            .ThenBy(static interval => interval.End)
            .ToList();

        var totalDuration = (maxTime - minTime).TotalMilliseconds;
        if (activeIntervals.Count is 0)
            return totalDuration;

        var coveredDuration = 0.0;
        var currentStart = activeIntervals[0].Start;
        var currentEnd = activeIntervals[0].End;

        for (var i = 1; i < activeIntervals.Count; i++)
        {
            var interval = activeIntervals[i];
            if (interval.Start <= currentEnd)
            {
                if (interval.End > currentEnd)
                {
                    currentEnd = interval.End;
                }

                continue;
            }

            coveredDuration += (currentEnd - currentStart).TotalMilliseconds;
            currentStart = interval.Start;
            currentEnd = interval.End;
        }

        coveredDuration += (currentEnd - currentStart).TotalMilliseconds;
        return Math.Max(0, totalDuration - coveredDuration);
    }

    private static string GetCss()
    {
        return @"
        :root {
            --header-height: 140px;
        }
        * {
            margin: 0;
            padding: 0;
            box-sizing: border-box;
        }
        body {
            font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, 'Helvetica Neue', Arial, sans-serif;
            background: #1e1e1e;
            color: #d4d4d4;
            overflow: hidden;
        }
        .header {
            padding: 20px;
            background: #252526;
            border-bottom: 1px solid #3e3e42;
        }
        .header h1 {
            font-size: 24px;
            margin-bottom: 8px;
        }
        .info {
            color: #858585;
            font-size: 14px;
        }
        .run-link {
            color: #58a6ff;
            text-decoration: none;
        }
        .run-link:hover {
            text-decoration: underline;
        }
        .header-controls {
            margin-top: 12px;
            display: flex;
            align-items: center;
            gap: 8px;
            flex-wrap: wrap;
            color: #cccccc;
            font-size: 13px;
        }
        .search-input {
            width: min(520px, 60vw);
            padding: 6px 10px;
            border-radius: 4px;
            border: 1px solid #3e3e42;
            background: #1e1e1e;
            color: #d4d4d4;
            outline: none;
        }
        .search-input:focus {
            border-color: #3794ff;
            box-shadow: 0 0 0 1px #3794ff;
        }
        .search-status {
            color: #9cdcfe;
            min-width: 90px;
        }
        .filters-separator {
            color: #4f4f55;
        }
        .filter-option {
            display: inline-flex;
            align-items: center;
            gap: 6px;
            color: #cccccc;
            user-select: none;
            white-space: nowrap;
            cursor: pointer;
        }
        .filter-option input {
            accent-color: #3794ff;
            cursor: pointer;
        }
        #container {
            position: absolute;
            top: var(--header-height);
            left: 0;
            right: 0;
            bottom: 0;
            overflow: hidden;
        }
        #container:active {
            cursor: grabbing;
        }
        #canvas {
            display: block;
        }
        .tooltip {
            position: fixed;
            background: #252526;
            border: 1px solid #3e3e42;
            border-radius: 4px;
            padding: 12px;
            pointer-events: none;
            display: none;
            width: max-content;
            max-width: min(900px, calc(100vw - 16px));
            max-height: calc(100vh - 16px);
            overflow: auto;
            overflow-wrap: anywhere;
            word-break: break-word;
            white-space: normal;
            z-index: 1000;
            font-size: 12px;
            box-shadow: 0 4px 8px rgba(0, 0, 0, 0.3);
        }
        .tooltip-title {
            font-weight: bold;
            margin-bottom: 8px;
            color: #ffffff;
        }
        .tooltip-row {
            margin: 4px 0;
        }
        .tooltip-label {
            color: #858585;
        }";
    }

    private static string GetJavaScript()
    {
        return @"
        const header = document.getElementById('header');
        const container = document.getElementById('container');
        const canvas = document.getElementById('canvas');
        const ctx = canvas.getContext('2d');
        const tooltip = document.getElementById('tooltip');
        const searchInput = document.getElementById('search');
        const searchStatus = document.getElementById('search-status');
        const filterMsbuildTargetsInput = document.getElementById('filter-msbuild-targets');
        const filterMsbuildTasksInput = document.getElementById('filter-msbuild-tasks');
        const filterTestsInput = document.getElementById('filter-tests');

        const ROW_HEIGHT = 16;
        const ROW_PADDING = 2;
        const LANE_INNER_PADDING = 4;
        const LANE_PADDING = 10;
        const LEFT_MARGIN = 200;
        const TOP_MARGIN = 60;
        const MIN_SPAN_WIDTH = 2;
        const ZOOM_MIN = 0.1;
        const ZOOM_MAX = 50;

        let zoom = 1;
        let panX = 0;
        let panY = 0;
        let isDragging = false;
        let lastX = 0;
        let lastY = 0;
        let hoveredSpan = null;
        let searchTerm = '';
        let matchedSpanIds = null;
        let showMsbuildTargets = true;
        let showMsbuildTasks = true;
        let showTests = true;

        const kindColors = {
            'workflow': '#3794ff',
            'job': '#22863a',
            'step': '#6f42c1',
            'log.group': '#b08800',
            'msbuild.target': '#005a9e',
            'msbuild.task': '#0078d4',
            'test': '#d73a49',
        };

        const spansById = new Map();
        spansData.forEach(span => {
            spansById.set(span.id, span);
        });

        function buildLayout() {
            const spanRows = new Map();
            const spanVariants = new Map();
            const laneHeights = new Array(jobsData.length).fill(ROW_HEIGHT + LANE_INNER_PADDING * 2);
            const laneOffsets = new Array(jobsData.length).fill(0);
            const spansByLane = new Map();

            spansData.forEach(span => {
                if (span.jobIndex < 0 || span.jobIndex >= jobsData.length) return;
                if (!isSpanVisibleByFilters(span)) return;

                if (!spansByLane.has(span.jobIndex)) {
                    spansByLane.set(span.jobIndex, []);
                }

                spansByLane.get(span.jobIndex).push(span);
            });

            spansByLane.forEach((laneSpans, laneIndex) => {
                laneSpans.sort((a, b) => {
                    if (a.start !== b.start) {
                        return a.start - b.start;
                    }

                    return a.duration - b.duration;
                });

                const rowEndTimes = [];
                const rowItemCounts = [];
                laneSpans.forEach(span => {
                    const spanEnd = span.start + span.duration;
                    let rowIndex = rowEndTimes.findIndex(endTime => span.start >= endTime);

                    if (rowIndex < 0) {
                        rowIndex = rowEndTimes.length;
                        rowEndTimes.push(spanEnd);
                    } else {
                        rowEndTimes[rowIndex] = spanEnd;
                    }

                    const rowItemCount = rowItemCounts[rowIndex] ?? 0;
                    spanVariants.set(span.id, rowItemCount % 2);
                    rowItemCounts[rowIndex] = rowItemCount + 1;
                    spanRows.set(span.id, rowIndex);
                });

                const rowCount = Math.max(1, rowEndTimes.length);
                laneHeights[laneIndex] = rowCount * ROW_HEIGHT + Math.max(0, rowCount - 1) * ROW_PADDING + LANE_INNER_PADDING * 2;
            });

            let offset = 0;
            for (let i = 0; i < jobsData.length; i++) {
                laneOffsets[i] = offset;
                offset += laneHeights[i];

                if (i < jobsData.length - 1) {
                    offset += LANE_PADDING;
                }
            }

            return {
                laneHeights,
                laneOffsets,
                spanRows,
                spanVariants,
                contentHeight: offset,
            };
        }

        let layout = buildLayout();

        function getDpr() {
            return window.devicePixelRatio || 1;
        }

        function updateHeaderHeight() {
            const headerHeight = header
                ? Math.max(100, Math.ceil(header.getBoundingClientRect().height))
                : 140;

            document.documentElement.style.setProperty('--header-height', `${headerHeight}px`);
        }

        function getCanvasWidth() {
            return canvas.width / getDpr();
        }

        function getCanvasHeight() {
            return canvas.height / getDpr();
        }

        function getTimelineWidth() {
            return Math.max(1, getCanvasWidth() - LEFT_MARGIN - 50);
        }

        function getSafeTotalDuration() {
            return totalDuration > 0 ? totalDuration : 1;
        }

        function getViewportLaneHeight() {
            return Math.max(1, getCanvasHeight() - TOP_MARGIN);
        }

        function clampPan() {
            const timelineWidth = getTimelineWidth();
            const dataWidth = timelineWidth * zoom;

            if (dataWidth <= timelineWidth) {
                panX = 0;
            } else {
                const minPanX = timelineWidth - dataWidth;
                const maxPanX = 0;
                panX = Math.min(maxPanX, Math.max(minPanX, panX));
            }

            const dataHeight = Math.max(ROW_HEIGHT, layout.contentHeight);
            const viewportLaneHeight = getViewportLaneHeight();

            if (dataHeight <= viewportLaneHeight) {
                panY = 0;
            } else {
                const minPanY = viewportLaneHeight - dataHeight;
                const maxPanY = 0;
                panY = Math.min(maxPanY, Math.max(minPanY, panY));
            }
        }

        function resizeCanvas() {
            updateHeaderHeight();

            const dpr = getDpr();
            canvas.width = container.clientWidth * dpr;
            canvas.height = container.clientHeight * dpr;
            canvas.style.width = container.clientWidth + 'px';
            canvas.style.height = container.clientHeight + 'px';
            ctx.setTransform(1, 0, 0, 1, 0, 0);
            ctx.scale(dpr, dpr);
            clampPan();
            draw();
        }

        function timeToX(time) {
            return LEFT_MARGIN + (time / getSafeTotalDuration()) * getTimelineWidth() * zoom + panX;
        }

        function durationToWidth(duration) {
            return Math.max(MIN_SPAN_WIDTH, (duration / getSafeTotalDuration()) * getTimelineWidth() * zoom);
        }

        function laneToY(laneIndex) {
            return TOP_MARGIN + layout.laneOffsets[laneIndex] + panY;
        }

        function adjustHexColor(hexColor, factor) {
            const color = hexColor.startsWith('#') ? hexColor.substring(1) : hexColor;
            if (color.length !== 6) {
                return hexColor;
            }

            const r = Number.parseInt(color.substring(0, 2), 16);
            const g = Number.parseInt(color.substring(2, 4), 16);
            const b = Number.parseInt(color.substring(4, 6), 16);

            const adjustChannel = channel => {
                if (factor >= 0) {
                    return Math.min(255, Math.round(channel + (255 - channel) * factor));
                }

                return Math.max(0, Math.round(channel * (1 + factor)));
            };

            const rr = adjustChannel(r).toString(16).padStart(2, '0');
            const gg = adjustChannel(g).toString(16).padStart(2, '0');
            const bb = adjustChannel(b).toString(16).padStart(2, '0');
            return `#${rr}${gg}${bb}`;
        }

        function getSpanStatus(span) {
            const attributeKeys = [
                'test.outcome',
                'step.conclusion',
                'job.conclusion',
                'conclusion',
                'outcome',
                'result',
                'step.status',
                'job.status',
                'status',
            ];

            for (const key of attributeKeys) {
                const value = span.attributes?.[key];
                if (!value) {
                    continue;
                }

                const normalizedValue = String(value).toLowerCase();
                if (['success', 'succeeded', 'passed', 'pass', 'ok', 'completed'].includes(normalizedValue)) {
                    return 'success';
                }

                if (['failure', 'failed', 'fail', 'error', 'cancelled', 'canceled', 'timed_out', 'timeout', 'startup_failure'].includes(normalizedValue)) {
                    return 'failure';
                }
            }

            return null;
        }

        function getSpanColor(span, variant) {
            const status = getSpanStatus(span);
            const baseColor = status === 'success'
                ? '#2ea043'
                : status === 'failure'
                    ? '#da3633'
                    : (kindColors[span.kind] || '#858585');

            const contrastFactor = variant === 0 ? -0.1 : 0.12;
            return adjustHexColor(baseColor, contrastFactor);
        }

        function formatDuration(ms) {
            if (ms >= 3600000) {
                const h = Math.floor(ms / 3600000);
                const m = Math.floor((ms % 3600000) / 60000);
                const s = Math.floor((ms % 60000) / 1000);
                return `${h}h ${m}m ${s}s`;
            }
            if (ms >= 60000) {
                const m = Math.floor(ms / 60000);
                const s = Math.floor((ms % 60000) / 1000);
                return `${m}m ${s}s`;
            }
            if (ms >= 1000) {
                return `${(ms / 1000).toFixed(3)}s`;
            }
            return `${ms.toFixed(0)}ms`;
        }

        function formatUtcTimestamp(epochMilliseconds) {
            const value = new Date(epochMilliseconds).toISOString();
            return value.replace('T', ' ').replace('Z', ' UTC');
        }

        function getSpanHierarchy(span) {
            const parts = [];
            const visited = new Set();
            let current = span;

            while (current && !visited.has(current.id)) {
                visited.add(current.id);
                parts.push(current.name || '(unnamed span)');

                if (current.parentId === null || current.parentId === undefined || current.parentId === 0) {
                    break;
                }

                current = spansById.get(current.parentId);
            }

            parts.reverse();
            return parts.join(', ');
        }

        function isSpanVisibleByFilters(span) {
            if (span.kind === 'msbuild.target') {
                return showMsbuildTargets;
            }

            if (span.kind === 'msbuild.task') {
                return showMsbuildTasks;
            }

            if (span.kind === 'test') {
                return showTests;
            }

            return true;
        }

        function updateFilters() {
            showMsbuildTargets = !filterMsbuildTargetsInput || filterMsbuildTargetsInput.checked;
            showMsbuildTasks = !filterMsbuildTasksInput || filterMsbuildTasksInput.checked;
            showTests = !filterTestsInput || filterTestsInput.checked;
            layout = buildLayout();

            updateSearchStatus();
            hoveredSpan = null;
            hideTooltip();
            draw();
        }

        function updateSearch(rawSearchTerm) {
            searchTerm = (rawSearchTerm ?? '').trim().toLowerCase();
            if (!searchTerm) {
                matchedSpanIds = null;
            } else {
                matchedSpanIds = new Set(
                    spansData
                        .filter(span => span.name && span.name.toLowerCase().includes(searchTerm))
                        .map(span => span.id)
                );
            }

            updateSearchStatus();
            hoveredSpan = null;
            hideTooltip();
            draw();
        }

        function updateSearchStatus() {
            if (!searchStatus) {
                return;
            }

            if (!searchTerm) {
                searchStatus.textContent = '';
                return;
            }

            const count = spansData.filter(span => isSpanVisibleByFilters(span) && isSearchMatch(span)).length;
            const suffix = count === 1 ? '' : 'es';
            searchStatus.textContent = `${count} match${suffix}`;
        }

        function isSearchMatch(span) {
            return matchedSpanIds === null || matchedSpanIds.has(span.id);
        }

        function draw() {
            clampPan();

            const canvasWidth = getCanvasWidth();
            const canvasHeight = getCanvasHeight();

            ctx.clearRect(0, 0, canvasWidth, canvasHeight);

            ctx.fillStyle = '#1e1e1e';
            ctx.fillRect(0, 0, canvasWidth, canvasHeight);

            ctx.fillStyle = '#252526';
            ctx.fillRect(0, 0, LEFT_MARGIN, canvasHeight);

            jobsData.forEach((job, index) => {
                const y = laneToY(index);
                const laneHeight = layout.laneHeights[index];
                
                ctx.fillStyle = '#2d2d30';
                ctx.fillRect(LEFT_MARGIN, y, canvasWidth - LEFT_MARGIN, laneHeight);

                ctx.fillStyle = '#d4d4d4';
                ctx.font = '12px sans-serif';
                ctx.textAlign = 'right';
                ctx.textBaseline = 'middle';
                ctx.fillText(job.name, LEFT_MARGIN - 10, y + laneHeight / 2);
            });

            const visibleSpans = [];
            spansData.forEach(span => {
                if (span.jobIndex < 0) return;
                if (!isSpanVisibleByFilters(span)) return;

                const rowIndex = layout.spanRows.get(span.id);
                if (rowIndex === undefined) return;

                const x = timeToX(span.start);
                const width = durationToWidth(span.duration);
                const y = laneToY(span.jobIndex) + LANE_INNER_PADDING + rowIndex * (ROW_HEIGHT + ROW_PADDING);
                const height = ROW_HEIGHT;

                if (x + width < LEFT_MARGIN || x > canvasWidth) return;
                if (y + height < TOP_MARGIN || y > canvasHeight) return;

                visibleSpans.push({ span, x, y, width, height });
            });

            const timelineSteps = [1, 2, 5, 10, 20, 50, 100, 200, 500, 1000, 2000, 5000, 10000, 20000, 50000, 100000];
            const pixelsPerMs = zoom * getTimelineWidth() / getSafeTotalDuration();
            let step = timelineSteps.find(s => s * pixelsPerMs > 50) || timelineSteps[timelineSteps.length - 1];

            ctx.fillStyle = '#3e3e42';
            for (let t = 0; t <= totalDuration; t += step) {
                const x = timeToX(t);
                if (x >= LEFT_MARGIN && x <= canvasWidth) {
                    ctx.fillRect(x, TOP_MARGIN, 1, canvasHeight - TOP_MARGIN);
                }
            }

            const hasSearch = searchTerm.length > 0;
            visibleSpans.forEach(({ span, x, y, width, height }) => {
                const isMatch = isSearchMatch(span);
                const variant = layout.spanVariants.get(span.id) ?? 0;
                if (hasSearch && !isMatch) {
                    ctx.globalAlpha = 0.18;
                    ctx.fillStyle = '#5a5a5a';
                } else {
                    ctx.globalAlpha = 1;
                    ctx.fillStyle = getSpanColor(span, variant);
                }

                ctx.fillRect(x, y, width, height);
                ctx.globalAlpha = 1;

                if (hasSearch && isMatch) {
                    ctx.strokeStyle = '#f2cc60';
                    ctx.lineWidth = 1;
                    ctx.strokeRect(x + 0.5, y + 0.5, Math.max(1, width - 1), Math.max(1, height - 1));
                }

                if (width > 50) {
                    ctx.fillStyle = hasSearch && !isMatch ? '#d4d4d4' : '#ffffff';
                    ctx.globalAlpha = hasSearch && !isMatch ? 0.85 : 1;
                    ctx.font = '11px sans-serif';
                    ctx.textAlign = 'left';
                    ctx.textBaseline = 'middle';
                    const text = span.name;
                    const maxTextWidth = width - 8;
                    const textWidth = ctx.measureText(text).width;
                    const displayText = textWidth > maxTextWidth 
                        ? text.substring(0, Math.floor(text.length * maxTextWidth / textWidth)) + '...'
                        : text;
                    ctx.fillText(displayText, x + 4, y + height / 2);
                    ctx.globalAlpha = 1;
                }
            });

            ctx.fillStyle = '#3e3e42';
            ctx.fillRect(0, TOP_MARGIN - 30, canvasWidth, 30);

            ctx.fillStyle = '#858585';
            ctx.font = '10px sans-serif';
            ctx.textAlign = 'center';
            for (let t = 0; t <= totalDuration; t += step) {
                const x = timeToX(t);
                if (x >= LEFT_MARGIN && x <= canvasWidth) {
                    ctx.fillText(formatDuration(t), x, TOP_MARGIN - 15);
                }
            }
        }

        function getSpanAt(x, y) {
            for (let i = spansData.length - 1; i >= 0; i--) {
                const span = spansData[i];
                if (span.jobIndex < 0) continue;
                if (!isSpanVisibleByFilters(span)) continue;

                const rowIndex = layout.spanRows.get(span.id);
                if (rowIndex === undefined) continue;

                const sx = timeToX(span.start);
                const sy = laneToY(span.jobIndex) + LANE_INNER_PADDING + rowIndex * (ROW_HEIGHT + ROW_PADDING);
                const width = durationToWidth(span.duration);
                const height = ROW_HEIGHT;

                if (x >= sx && x <= sx + width && y >= sy && y <= sy + height) {
                    return span;
                }
            }
            return null;
        }

        function showTooltip(span, mouseX, mouseY) {
            const spanStartUtc = minTime + span.start;
            const spanEndUtc = spanStartUtc + span.duration;
            const lines = [
                `<div class='tooltip-title'>${span.name}</div>`,
                `<div class='tooltip-row'><span class='tooltip-label'>Kind:</span> ${span.kind}</div>`,
                `<div class='tooltip-row'><span class='tooltip-label'>Hierarchy:</span> ${getSpanHierarchy(span)}</div>`,
                `<div class='tooltip-row'><span class='tooltip-label'>Duration:</span> ${formatDuration(span.duration)}</div>`,
                `<div class='tooltip-row'><span class='tooltip-label'>Start time (UTC):</span> ${formatUtcTimestamp(spanStartUtc)}</div>`,
                `<div class='tooltip-row'><span class='tooltip-label'>End time (UTC):</span> ${formatUtcTimestamp(spanEndUtc)}</div>`,
            ];

            Object.entries(span.attributes).forEach(([key, value]) => {
                if (value && value.trim()) {
                    lines.push(`<div class='tooltip-row'><span class='tooltip-label'>${key}:</span> ${value}</div>`);
                }
            });

            tooltip.innerHTML = lines.join('');
            tooltip.style.display = 'block';
            positionTooltip(mouseX, mouseY);
        }

        function positionTooltip(mouseX, mouseY) {
            const tooltipOffset = 10;
            const viewportPadding = 8;
            const rect = tooltip.getBoundingClientRect();

            let left = mouseX + tooltipOffset;
            let top = mouseY + tooltipOffset;

            if (left + rect.width > window.innerWidth - viewportPadding) {
                left = mouseX - rect.width - tooltipOffset;
            }

            if (top + rect.height > window.innerHeight - viewportPadding) {
                top = mouseY - rect.height - tooltipOffset;
            }

            left = Math.max(viewportPadding, Math.min(left, window.innerWidth - rect.width - viewportPadding));
            top = Math.max(viewportPadding, Math.min(top, window.innerHeight - rect.height - viewportPadding));

            tooltip.style.left = left + 'px';
            tooltip.style.top = top + 'px';
        }

        function hideTooltip() {
            tooltip.style.display = 'none';
        }

        canvas.addEventListener('wheel', (e) => {
            e.preventDefault();

            if (e.ctrlKey || e.metaKey) {
                const rect = canvas.getBoundingClientRect();
                const mouseX = e.clientX - rect.left;
                const oldZoom = zoom;

                zoom *= e.deltaY > 0 ? 0.9 : 1.1;
                zoom = Math.max(ZOOM_MIN, Math.min(zoom, ZOOM_MAX));

                const zoomPoint = (mouseX - LEFT_MARGIN - panX) / oldZoom;
                panX = mouseX - LEFT_MARGIN - zoomPoint * zoom;
            } else {
                const useShiftAsHorizontal = e.shiftKey && Math.abs(e.deltaX) < Math.abs(e.deltaY);
                const horizontalDelta = useShiftAsHorizontal ? e.deltaY : e.deltaX;
                const verticalDelta = useShiftAsHorizontal ? 0 : e.deltaY;

                panX -= horizontalDelta;
                panY -= verticalDelta;
            }

            clampPan();
            draw();
            hideTooltip();
        }, { passive: false });

        canvas.addEventListener('mousedown', (e) => {
            if (e.button !== 0) {
                return;
            }

            isDragging = true;
            lastX = e.clientX;
            lastY = e.clientY;
        });

        canvas.addEventListener('mousemove', (e) => {
            const rect = canvas.getBoundingClientRect();
            const mouseX = e.clientX - rect.left;
            const mouseY = e.clientY - rect.top;

            if (isDragging) {
                const dx = e.clientX - lastX;
                const dy = e.clientY - lastY;
                panX += dx;
                panY += dy;
                lastX = e.clientX;
                lastY = e.clientY;
                clampPan();
                draw();
                hideTooltip();
            } else {
                const span = getSpanAt(mouseX, mouseY);
                if (span !== hoveredSpan) {
                    hoveredSpan = span;
                    if (span) {
                        showTooltip(span, e.clientX, e.clientY);
                    } else {
                        hideTooltip();
                    }
                } else if (span) {
                    positionTooltip(e.clientX, e.clientY);
                }
            }
        });

        canvas.addEventListener('mouseup', () => {
            isDragging = false;
        });

        window.addEventListener('mouseup', () => {
            isDragging = false;
        });

        canvas.addEventListener('mouseleave', () => {
            isDragging = false;
            hideTooltip();
        });

        if (searchInput) {
            searchInput.addEventListener('input', () => {
                updateSearch(searchInput.value);
            });

            searchInput.addEventListener('keydown', (e) => {
                if (e.key !== 'Escape') {
                    return;
                }

                searchInput.value = '';
                updateSearch('');
                searchInput.blur();
            });
        }

        if (filterMsbuildTargetsInput) {
            filterMsbuildTargetsInput.addEventListener('change', updateFilters);
        }

        if (filterMsbuildTasksInput) {
            filterMsbuildTasksInput.addEventListener('change', updateFilters);
        }

        if (filterTestsInput) {
            filterTestsInput.addEventListener('change', updateFilters);
        }

        showMsbuildTargets = !filterMsbuildTargetsInput || filterMsbuildTargetsInput.checked;
        showMsbuildTasks = !filterMsbuildTasksInput || filterMsbuildTasksInput.checked;
        showTests = !filterTestsInput || filterTestsInput.checked;
        layout = buildLayout();

        window.addEventListener('keydown', (e) => {
            if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === 'f' && searchInput) {
                e.preventDefault();
                searchInput.focus();
                searchInput.select();
            }
        });

        window.addEventListener('resize', resizeCanvas);
        resizeCanvas();";
    }
}
