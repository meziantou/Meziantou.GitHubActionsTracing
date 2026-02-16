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

        var spansData = BuildSpansData(spans, minTime, jobs);
        var jobsData = BuildJobsData(jobs);

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
        html.AppendLine("    <div class=\"header\">");
        html.AppendLine($"        <h1>{HtmlEncode(model.WorkflowRun.Name ?? "Workflow Run")}</h1>");
        html.AppendLine(CultureInfo.InvariantCulture, $"        <div class=\"info\">Duration: {FormatDuration((maxTime - minTime).TotalMilliseconds)} | Spans: {spans.Count} | Jobs: {jobs.Count}</div>");
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

    private static string GetCss()
    {
        return @"
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
        #container {
            position: absolute;
            top: 100px;
            left: 0;
            right: 0;
            bottom: 0;
            overflow: auto;
            cursor: grab;
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
            max-width: 400px;
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
        const container = document.getElementById('container');
        const canvas = document.getElementById('canvas');
        const ctx = canvas.getContext('2d');
        const tooltip = document.getElementById('tooltip');

        const ROW_HEIGHT = 16;
        const ROW_PADDING = 2;
        const LANE_INNER_PADDING = 4;
        const LANE_PADDING = 10;
        const LEFT_MARGIN = 200;
        const TOP_MARGIN = 60;
        const MIN_SPAN_WIDTH = 2;

        let zoom = 1;
        let panX = 0;
        let panY = 0;
        let isDragging = false;
        let lastX = 0;
        let lastY = 0;
        let hoveredSpan = null;

        const kindColors = {
            'workflow': '#3794ff',
            'job': '#22863a',
            'step': '#6f42c1',
            'log.group': '#b08800',
            'msbuild.target': '#005a9e',
            'msbuild.task': '#0078d4',
            'test': '#d73a49',
        };

        function buildLayout() {
            const spanRows = new Map();
            const spanVariants = new Map();
            const laneHeights = new Array(jobsData.length).fill(ROW_HEIGHT + LANE_INNER_PADDING * 2);
            const laneOffsets = new Array(jobsData.length).fill(0);
            const spansByLane = new Map();

            spansData.forEach(span => {
                if (span.jobIndex < 0 || span.jobIndex >= jobsData.length) return;

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
                totalHeight: TOP_MARGIN + offset + 100,
            };
        }

        const layout = buildLayout();

        function resizeCanvas() {
            const dpr = window.devicePixelRatio || 1;
            canvas.width = container.clientWidth * dpr;
            canvas.height = Math.max(
                container.clientHeight * dpr,
                layout.totalHeight * dpr
            );
            canvas.style.width = container.clientWidth + 'px';
            canvas.style.height = (canvas.height / dpr) + 'px';
            ctx.scale(dpr, dpr);
            draw();
        }

        function timeToX(time) {
            return LEFT_MARGIN + (time / totalDuration) * (canvas.width / window.devicePixelRatio - LEFT_MARGIN - 50) * zoom + panX;
        }

        function durationToWidth(duration) {
            return Math.max(MIN_SPAN_WIDTH, (duration / totalDuration) * (canvas.width / window.devicePixelRatio - LEFT_MARGIN - 50) * zoom);
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

        function draw() {
            ctx.clearRect(0, 0, canvas.width, canvas.height);

            ctx.fillStyle = '#1e1e1e';
            ctx.fillRect(0, 0, canvas.width, canvas.height);

            ctx.fillStyle = '#252526';
            ctx.fillRect(0, 0, LEFT_MARGIN, canvas.height);

            jobsData.forEach((job, index) => {
                const y = laneToY(index);
                const laneHeight = layout.laneHeights[index];
                
                ctx.fillStyle = '#2d2d30';
                ctx.fillRect(LEFT_MARGIN, y, canvas.width - LEFT_MARGIN, laneHeight);

                ctx.fillStyle = '#d4d4d4';
                ctx.font = '12px sans-serif';
                ctx.textAlign = 'right';
                ctx.textBaseline = 'middle';
                ctx.fillText(job.name, LEFT_MARGIN - 10, y + laneHeight / 2);
            });

            const visibleSpans = [];
            spansData.forEach(span => {
                if (span.jobIndex < 0) return;

                const rowIndex = layout.spanRows.get(span.id);
                if (rowIndex === undefined) return;

                const x = timeToX(span.start);
                const width = durationToWidth(span.duration);
                const y = laneToY(span.jobIndex) + LANE_INNER_PADDING + rowIndex * (ROW_HEIGHT + ROW_PADDING);
                const height = ROW_HEIGHT;

                if (x + width < LEFT_MARGIN || x > canvas.width) return;
                if (y + height < TOP_MARGIN || y > canvas.height) return;

                visibleSpans.push({ span, x, y, width, height });
            });

            visibleSpans.forEach(({ span, x, y, width, height }) => {
                const variant = layout.spanVariants.get(span.id) ?? 0;
                ctx.fillStyle = getSpanColor(span, variant);
                ctx.fillRect(x, y, width, height);

                if (width > 50) {
                    ctx.fillStyle = '#ffffff';
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
                }
            });

            ctx.fillStyle = '#3e3e42';
            ctx.fillRect(0, TOP_MARGIN - 30, canvas.width, 30);
            
            const timelineSteps = [1, 2, 5, 10, 20, 50, 100, 200, 500, 1000, 2000, 5000, 10000, 20000, 50000, 100000];
            const pixelsPerMs = zoom * (canvas.width / window.devicePixelRatio - LEFT_MARGIN - 50) / totalDuration;
            let step = timelineSteps.find(s => s * pixelsPerMs > 50) || timelineSteps[timelineSteps.length - 1];

            ctx.fillStyle = '#858585';
            ctx.font = '10px sans-serif';
            ctx.textAlign = 'center';
            for (let t = 0; t <= totalDuration; t += step) {
                const x = timeToX(t);
                if (x >= LEFT_MARGIN && x <= canvas.width) {
                    ctx.fillText(formatDuration(t), x, TOP_MARGIN - 15);
                    ctx.fillStyle = '#3e3e42';
                    ctx.fillRect(x, TOP_MARGIN, 1, canvas.height - TOP_MARGIN);
                    ctx.fillStyle = '#858585';
                }
            }
        }

        function getSpanAt(x, y) {
            for (let i = spansData.length - 1; i >= 0; i--) {
                const span = spansData[i];
                if (span.jobIndex < 0) continue;

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
            const lines = [
                `<div class='tooltip-title'>${span.name}</div>`,
                `<div class='tooltip-row'><span class='tooltip-label'>Kind:</span> ${span.kind}</div>`,
                `<div class='tooltip-row'><span class='tooltip-label'>Duration:</span> ${formatDuration(span.duration)}</div>`,
            ];

            Object.entries(span.attributes).forEach(([key, value]) => {
                if (value && value.trim()) {
                    lines.push(`<div class='tooltip-row'><span class='tooltip-label'>${key}:</span> ${value}</div>`);
                }
            });

            tooltip.innerHTML = lines.join('');
            tooltip.style.display = 'block';
            tooltip.style.left = (mouseX + 10) + 'px';
            tooltip.style.top = (mouseY + 10) + 'px';
        }

        function hideTooltip() {
            tooltip.style.display = 'none';
        }

        canvas.addEventListener('wheel', (e) => {
            e.preventDefault();
            const rect = canvas.getBoundingClientRect();
            const mouseX = e.clientX - rect.left;
            const oldZoom = zoom;
            
            zoom *= e.deltaY > 0 ? 0.9 : 1.1;
            zoom = Math.max(0.1, Math.min(zoom, 50));

            const zoomPoint = (mouseX - LEFT_MARGIN - panX) / oldZoom;
            panX = mouseX - LEFT_MARGIN - zoomPoint * zoom;

            draw();
        });

        canvas.addEventListener('mousedown', (e) => {
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
                    tooltip.style.left = (e.clientX + 10) + 'px';
                    tooltip.style.top = (e.clientY + 10) + 'px';
                }
            }
        });

        canvas.addEventListener('mouseup', () => {
            isDragging = false;
        });

        canvas.addEventListener('mouseleave', () => {
            isDragging = false;
            hideTooltip();
        });

        window.addEventListener('resize', resizeCanvas);
        resizeCanvas();";
    }
}
