using System.Text.Json.Serialization;

namespace Meziantou.GitHubActionsTracing;

internal sealed class WorkflowRunResponse
{
    [JsonPropertyName("id")]
    public long Id { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("display_title")]
    public string? DisplayTitle { get; init; }

    [JsonPropertyName("html_url")]
    public string? HtmlUrl { get; init; }

    [JsonPropertyName("event")]
    public string? Event { get; init; }

    [JsonPropertyName("head_branch")]
    public string? HeadBranch { get; init; }

    [JsonPropertyName("head_sha")]
    public string? HeadSha { get; init; }

    [JsonPropertyName("run_number")]
    public int? RunNumber { get; init; }

    [JsonPropertyName("run_attempt")]
    public int? RunAttempt { get; init; }

    [JsonPropertyName("created_at")]
    public DateTimeOffset? CreatedAt { get; init; }

    [JsonPropertyName("run_started_at")]
    public DateTimeOffset? RunStartedAt { get; init; }

    [JsonPropertyName("updated_at")]
    public DateTimeOffset? UpdatedAt { get; init; }

    [JsonPropertyName("repository")]
    public WorkflowRepository? Repository { get; init; }
}
