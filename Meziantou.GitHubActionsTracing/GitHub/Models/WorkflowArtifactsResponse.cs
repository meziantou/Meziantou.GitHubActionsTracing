using System.Text.Json.Serialization;

namespace Meziantou.GitHubActionsTracing;

internal sealed class WorkflowArtifactsResponse
{
    [JsonPropertyName("artifacts")]
    public List<WorkflowRunArtifact>? Artifacts { get; init; }
}
