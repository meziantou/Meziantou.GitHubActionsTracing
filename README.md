# Meziantou.GitHubActionsTracing

Generates trace files from a GitHub Actions run URL, including jobs, steps, log groups, warnings/errors, and TRX test results found in run artifacts.

## Requirements

- .NET 10 SDK
- GitHub authentication via the `GITHUB_TOKEN` environment variable or the `gh` CLI (`gh token show`).

## Installation

```bash
dotnet tool update --global Meziantou.GitHubActionsTracing
```

## Usage

```bash
Meziantou.GitHubActionsTracing <command> [options]
```

From source:

```powershell
dotnet run --project .\Meziantou.GitHubActionsTracing\ -- <command> [options]
```

### `export` command

```bash
Meziantou.GitHubActionsTracing export <workflow-run-url-or-folder> [options]
```

Options:

- `--format` selects one output format preset: `chromium`, `speedscope`, `otel`, `otel-file`.
- `--otel-endpoint` exports to an OTLP endpoint.
- `--otel-protocol` selects OTLP protocol (`grpc`, `http`, `http/protobuf`).
- `--otel-path` / `--otel-file-path` writes OpenTelemetry data to a file.
- `--chromium-path` writes Chromium trace output to a file.
- `--speedscope-path` writes Speedscope output to a file.
- `--minimum-test-duration` filters out short test spans.
- `--minimum-binlog-duration` / `--minimum-target-duration` filters out short binlog target spans.
- `--include-binlog` includes binlog targets and tasks (default: `true`).
- `--include-tests` includes TRX/JUnit test spans (default: `true`).

OpenTelemetry environment variables for workflow-run export use an `EXPORTER_` prefix (for example `EXPORTER_OTEL_SERVICE_NAME`, `EXPORTER_OTEL_EXPORTER_OTLP_ENDPOINT`, `EXPORTER_OTEL_EXPORTER_OTLP_PROTOCOL`).
This keeps exporter settings isolated from the host service/runtime `OTEL_*` configuration, which can point to a different collector.

Examples:

```bash
Meziantou.GitHubActionsTracing export https://github.com/OWNER/REPO/actions/runs/123456 --format chromium
Meziantou.GitHubActionsTracing export https://github.com/OWNER/REPO/actions/runs/123456 --otel-endpoint http://localhost:4317
Meziantou.GitHubActionsTracing export https://github.com/OWNER/REPO/actions/runs/123456 --otel-endpoint http://localhost:4317 --otel-protocol grpc
Meziantou.GitHubActionsTracing export https://github.com/OWNER/REPO/actions/runs/123456 --otel-endpoint http://localhost:4317 --otel-protocol http
Meziantou.GitHubActionsTracing export https://github.com/OWNER/REPO/actions/runs/123456 --otel-endpoint http://localhost:4317 --otel-protocol http/protobuf
Meziantou.GitHubActionsTracing export https://github.com/OWNER/REPO/actions/runs/123456 --otel-path trace.otel.json
Meziantou.GitHubActionsTracing export https://github.com/OWNER/REPO/actions/runs/123456 --chromium-path trace.json
Meziantou.GitHubActionsTracing export https://github.com/OWNER/REPO/actions/runs/123456 --speedscope-path trace.speedscope.json
Meziantou.GitHubActionsTracing export https://github.com/OWNER/REPO/actions/runs/123456 --chromium-path trace.json --speedscope-path trace.speedscope.json --otel-path trace.otel.json
```

```bash
Meziantou.GitHubActionsTracing download-run-info https://github.com/OWNER/REPO/actions/runs/123456 --output ./run-info
Meziantou.GitHubActionsTracing export ./run-info --chromium-path trace.json
```

```bash
export EXPORTER_OTEL_EXPORTER_OTLP_ENDPOINT=http://localhost:4317
Meziantou.GitHubActionsTracing export https://github.com/OWNER/REPO/actions/runs/123456 --format otel
```

```powershell
$env:EXPORTER_OTEL_EXPORTER_OTLP_ENDPOINT = "http://localhost:4317"
Meziantou.GitHubActionsTracing export https://github.com/OWNER/REPO/actions/runs/123456 --format otel
```

Default output paths:

- `trace-<runId>.json` for Chromium
- `trace-<runId>.otel.json` for OpenTelemetry
- `trace-<runId>.speedscope.json` for Speedscope

```bash
Meziantou.GitHubActionsTracing export https://github.com/OWNER/REPO/actions/runs/123456 --format otel-file
Meziantou.GitHubActionsTracing export https://github.com/OWNER/REPO/actions/runs/123456 --format chromium
Meziantou.GitHubActionsTracing export https://github.com/OWNER/REPO/actions/runs/123456 --format speedscope
```

```bash
Meziantou.GitHubActionsTracing export https://github.com/OWNER/REPO/actions/runs/123456 --format otel --minimum-test-duration 00:00:01 --minimum-binlog-duration 00:00:01 --include-binlog --include-tests
Meziantou.GitHubActionsTracing export https://github.com/OWNER/REPO/actions/runs/123456 --format chromium --include-binlog false --include-tests false
```

### `download-run-info` command

```bash
Meziantou.GitHubActionsTracing download-run-info https://github.com/OWNER/REPO/actions/runs/123456 --output ./run-info
```

A workflow job URL resolves to the same workflow run and downloads the full run data.

## Web API webhook receiver

An application is available in [Meziantou.GitHubActionsTracing.WebApi/](Meziantou.GitHubActionsTracing.WebApi).

Run it from source:

```powershell
dotnet run --project .\Meziantou.GitHubActionsTracing.WebApi\
```

The webhook endpoint is:

- `POST /webhooks/github`
- `POST /workflow-runs`

The API accepts `workflow_run` webhooks and queues runs when `action` is `completed`.
You can also queue a run manually by calling `POST /workflow-runs` with a JSON body such as
`{"workflowRunUrl":"https://github.com/OWNER/REPO/actions/runs/123456","deliveryId":"manual-request"}`.
Processing runs are exported using the same trace pipeline as the CLI.

Configuration is bound from `GitHubActionsTracingWebhook` using standard ASP.NET Core configuration sources (`appsettings*.json`, environment variables, command line, etc.).

Use these keys in `appsettings.json`:

- `GitHubActionsTracingWebhook:WebhookSecret` (recommended): validates `X-Hub-Signature-256`
- `GitHubActionsTracingWebhook:OtelEndpoint`: OTLP destination endpoint for exported workflow runs
- `GitHubActionsTracingWebhook:OtelProtocol`: `grpc`, `http`, `http/protobuf`
- `GitHubActionsTracingWebhook:OtelPath`: optional OTEL file output path
- `GitHubActionsTracingWebhook:ChromiumPath`: optional Chromium trace path
- `GitHubActionsTracingWebhook:SpeedscopePath`: optional Speedscope trace path
- `GitHubActionsTracingWebhook:HtmlPath`: optional HTML trace path
- `GitHubActionsTracingWebhook:IncludeBinlog`: `true`/`false`
- `GitHubActionsTracingWebhook:IncludeTests`: `true`/`false`
- `GitHubActionsTracingWebhook:MaxDegreeOfParallelism`: max number of workflow runs processed in parallel (default: `1`)
- `GitHubActionsTracingWebhook:MinimumTestDuration`: e.g. `00:00:01`
- `GitHubActionsTracingWebhook:MinimumBinlogDuration`: e.g. `00:00:01`

For environment variables, replace `:` with `__` (double underscore), for example:

- `GitHubActionsTracingWebhook__WebhookSecret`
- `GitHubActionsTracingWebhook__OtelEndpoint`

OpenTelemetry exporter environment variables with the `EXPORTER_` prefix (for example `EXPORTER_OTEL_EXPORTER_OTLP_ENDPOINT`, `EXPORTER_OTEL_EXPORTER_OTLP_PROTOCOL`, `EXPORTER_OTEL_SERVICE_NAME`) are still supported by the exporter pipeline.
