using System.Text.Json;
using Meziantou.GitHubActionsTracing.Server;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

builder.Services
    .AddOptions<WebhookProcessingOptions>()
    .Bind(builder.Configuration.GetSection(WebhookProcessingOptions.SectionName))
    .ValidateOnStart();

builder.Services.AddSingleton<IWorkflowRunProcessingQueue, WorkflowRunProcessingQueue>();
builder.Services.AddHostedService<WorkflowRunProcessingHostedService>();

var app = builder.Build();

var startupOptions = app.Services.GetRequiredService<IOptions<WebhookProcessingOptions>>().Value;
if (string.IsNullOrWhiteSpace(startupOptions.WebhookSecret))
{
    app.Logger.LogWarning("{SectionName}:WebhookSecret is not configured. Incoming webhook payloads will not be authenticated.", WebhookProcessingOptions.SectionName);
}

app.MapGet("/", () => TypedResults.Ok());

app.MapPost("/webhooks/github", async (
    HttpRequest request,
    IWorkflowRunProcessingQueue queue,
    IOptions<WebhookProcessingOptions> optionsAccessor,
    ILoggerFactory loggerFactory,
    CancellationToken cancellationToken) =>
{
    var logger = loggerFactory.CreateLogger("GitHubWebhook");
    var payloadBytes = await ReadRequestBodyAsync(request, cancellationToken);

    if (!GitHubWebhookSignatureValidator.IsValid(payloadBytes, request.Headers, optionsAccessor.Value.WebhookSecret))
    {
        logger.LogWarning("Rejected webhook because of an invalid signature");
        return Results.Unauthorized();
    }

    var eventName = request.Headers["X-GitHub-Event"].ToString();
    if (!string.Equals(eventName, "workflow_run", StringComparison.OrdinalIgnoreCase))
    {
        return Results.Ok(new WebhookResponse("ignored", $"Unsupported event type '{eventName}'"));
    }

    var payload = JsonSerializer.Deserialize<WorkflowRunWebhookPayload>(payloadBytes, WorkflowRunWebhookPayload.JsonSerializerOptions);
    if (payload is null)
    {
        return Results.BadRequest(new WebhookResponse("error", "Invalid JSON payload"));
    }

    if (!string.Equals(payload.Action, "completed", StringComparison.OrdinalIgnoreCase))
    {
        return Results.Ok(new WebhookResponse("ignored", $"Unsupported action '{payload.Action}'"));
    }

    var workflowRunUrl = payload.GetWorkflowRunUrl();
    if (workflowRunUrl is null)
    {
        return Results.BadRequest(new WebhookResponse("error", "Unable to resolve workflow run URL from payload"));
    }

    var deliveryId = request.Headers["X-GitHub-Delivery"].ToString();
    await queue.EnqueueAsync(new WorkflowRunProcessingItem(workflowRunUrl, deliveryId), cancellationToken);

    logger.LogInformation("Workflow run {WorkflowRunUrl} queued (delivery: {DeliveryId})", workflowRunUrl, deliveryId);
    return Results.Accepted(value: new WebhookResponse("queued", workflowRunUrl.AbsoluteUri));
});

app.Run();

static async Task<byte[]> ReadRequestBodyAsync(HttpRequest request, CancellationToken cancellationToken)
{
    await using var memoryStream = new MemoryStream();
    await request.Body.CopyToAsync(memoryStream, cancellationToken);
    return memoryStream.ToArray();
}
