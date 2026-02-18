namespace Meziantou.GitHubActionsTracing.Exporters;

internal interface ITraceExporter
{
    Task ExportAsync(TraceModel model);
}
