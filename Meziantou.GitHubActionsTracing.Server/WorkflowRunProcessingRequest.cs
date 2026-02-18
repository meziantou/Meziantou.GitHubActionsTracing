namespace Meziantou.GitHubActionsTracing.Server;

internal sealed class WorkflowRunProcessingRequest
{
    public string? WorkflowRunUrl { get; init; }

    public string? DeliveryId { get; init; }
}
