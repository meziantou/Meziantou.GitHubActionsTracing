using System.Text.Json.Serialization;

namespace Meziantou.GitHubActionsTracing;

internal sealed class WorkflowRepository
{
    [JsonPropertyName("id")]
    public long Id { get; init; }

    [JsonPropertyName("full_name")]
    public string? FullName { get; init; }
}
