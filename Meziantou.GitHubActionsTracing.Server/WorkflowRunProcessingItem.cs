namespace Meziantou.GitHubActionsTracing.Server;

internal readonly record struct WorkflowRunProcessingItem(Uri WorkflowRunUrl, string? DeliveryId);
