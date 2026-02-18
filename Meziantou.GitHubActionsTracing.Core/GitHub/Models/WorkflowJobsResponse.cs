using System.Text.Json.Serialization;

namespace Meziantou.GitHubActionsTracing;

internal sealed class WorkflowJobsResponse
{
    [JsonPropertyName("jobs")]
    public List<WorkflowRunJob>? Jobs { get; init; }
}
