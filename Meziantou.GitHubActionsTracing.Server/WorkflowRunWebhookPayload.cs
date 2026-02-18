using System.Text.Json;
using System.Text.Json.Serialization;

namespace Meziantou.GitHubActionsTracing.Server;

internal sealed class WorkflowRunWebhookPayload
{
    public static readonly JsonSerializerOptions JsonSerializerOptions = new(JsonSerializerDefaults.Web);

    [JsonPropertyName("action")]
    public string? Action { get; init; }

    [JsonPropertyName("workflow_run")]
    public WorkflowRunPayload? WorkflowRun { get; init; }

    [JsonPropertyName("repository")]
    public RepositoryPayload? Repository { get; init; }

    public Uri? GetWorkflowRunUrl()
    {
        if (!string.IsNullOrWhiteSpace(WorkflowRun?.HtmlUrl) &&
            Uri.TryCreate(WorkflowRun.HtmlUrl, UriKind.Absolute, out var workflowRunUri))
        {
            return workflowRunUri;
        }

        if (!string.IsNullOrWhiteSpace(Repository?.FullName) && WorkflowRun?.Id is > 0)
        {
            return new Uri(string.Create(CultureInfo.InvariantCulture, $"https://github.com/{Repository.FullName}/actions/runs/{WorkflowRun.Id}"), UriKind.Absolute);
        }

        return null;
    }

    internal sealed class WorkflowRunPayload
    {
        [JsonPropertyName("id")]
        public long Id { get; init; }

        [JsonPropertyName("html_url")]
        public string? HtmlUrl { get; init; }

        [JsonPropertyName("status")]
        public string? Status { get; init; }

        [JsonPropertyName("conclusion")]
        public string? Conclusion { get; init; }
    }

    internal sealed class RepositoryPayload
    {
        [JsonPropertyName("full_name")]
        public string? FullName { get; init; }
    }
}
