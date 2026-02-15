namespace Meziantou.GitHubActionsTracing.Exporters;

internal static class TraceExporter
{
    public static async Task ExportAsync(TraceModel model, IEnumerable<ITraceExporter> exporters)
    {
        foreach (var exporter in exporters)
        {
            await exporter.ExportAsync(model);
        }
    }
}
