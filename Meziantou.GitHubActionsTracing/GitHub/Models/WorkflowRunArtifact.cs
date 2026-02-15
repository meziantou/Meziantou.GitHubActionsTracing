using System.Text.Json.Serialization;

namespace Meziantou.GitHubActionsTracing;

internal sealed class WorkflowRunArtifact
{
    [JsonPropertyName("id")]
    public long Id { get; init; }

    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("size_in_bytes")]
    public long SizeInBytes { get; init; }

    [JsonPropertyName("created_at")]
    public DateTimeOffset? CreatedAt { get; init; }

    [JsonPropertyName("updated_at")]
    public DateTimeOffset? UpdatedAt { get; init; }
}
