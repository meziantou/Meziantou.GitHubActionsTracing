using Meziantou.Framework;

namespace Meziantou.GitHubActionsTracing;

internal sealed class ArtifactContext
{
    public WorkflowRunArtifact Artifact { get; init; } = null!;

    public FullPath Directory { get; init; }

    public FullPath FilesDirectory { get; init; }

    public List<FullPath> Files { get; init; } = [];

    public ArtifactHints Hints { get; init; } = new();

    public long? MappedJobId { get; set; }
}
