namespace Meziantou.GitHubActionsTracing;

internal sealed class ArtifactHints
{
    public HashSet<string> MachineNames { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, string> CustomProperties { get; } = new(StringComparer.OrdinalIgnoreCase);

    public string? GitHubJobId { get; set; }

    public string? RunnerName { get; set; }
}
