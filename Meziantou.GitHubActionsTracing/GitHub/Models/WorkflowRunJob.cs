using System.Text.Json.Serialization;

namespace Meziantou.GitHubActionsTracing;

internal sealed class WorkflowRunJob
{
    [JsonPropertyName("id")]
    public long Id { get; init; }

    [JsonPropertyName("run_id")]
    public long RunId { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("status")]
    public string? Status { get; init; }

    [JsonPropertyName("conclusion")]
    public string? Conclusion { get; init; }

    [JsonPropertyName("runner_name")]
    public string? RunnerName { get; init; }

    [JsonPropertyName("runner_group_name")]
    public string? RunnerGroupName { get; init; }

    [JsonPropertyName("labels")]
    public List<string>? Labels { get; init; }

    [JsonPropertyName("created_at")]
    public DateTimeOffset? CreatedAt { get; init; }

    [JsonPropertyName("started_at")]
    public DateTimeOffset? StartedAt { get; init; }

    [JsonPropertyName("completed_at")]
    public DateTimeOffset? CompletedAt { get; init; }

    [JsonPropertyName("steps")]
    public List<WorkflowRunStep>? Steps { get; init; }
}
