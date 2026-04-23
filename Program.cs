using FoundryHealthDemo.Models;
using FoundryHealthDemo.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();
builder.Services.AddHttpClient<FoundryAgentService>();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();

app.MapPost("/api/health-tips", async (HealthTipRequest request, FoundryAgentService agentService, CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(request.Message))
    {
        return Results.BadRequest(new { error = "message is required." });
    }

    try
    {
        var response = await agentService.GetHealthTipAsync(request.Message, cancellationToken);
        return Results.Ok(new HealthTipResponse(response));
    }
    catch (InvalidOperationException ex)
    {
        return Results.Problem(
            detail: ex.Message,
            title: "Configuration error",
            statusCode: StatusCodes.Status500InternalServerError);
    }
    catch (HttpRequestException ex)
    {
        return Results.Problem(
            detail: ex.Message,
            title: "Failed to call Foundry model endpoint",
            statusCode: StatusCodes.Status502BadGateway);
    }
})
.WithName("GetHealthTips");

app.MapPost("/api/health-agent-tips", async (HealthAgentRequest request, HttpContext httpContext, FoundryAgentService agentService, CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(request.Message))
    {
        return Results.BadRequest(new { error = "message is required." });
    }

    try
    {
        var bearerToken = httpContext.Request.Headers.Authorization.ToString();
        var result = await agentService.GetHealthTipFromAgentAsync(request.Message, request.ThreadId, bearerToken, cancellationToken);
        var structured = FoundryAgentService.ParseStructuredAgentResponse(result.Response);
        return Results.Ok(new HealthAgentResponse(structured.Type, structured.Message, result.ThreadId, result.Response));
    }
    catch (InvalidOperationException ex)
    {
        return Results.Problem(
            detail: ex.Message,
            title: "Configuration error",
            statusCode: StatusCodes.Status500InternalServerError);
    }
    catch (HttpRequestException ex)
    {
        return Results.Problem(
            detail: ex.Message,
            title: "Failed to call Foundry agent endpoint",
            statusCode: StatusCodes.Status502BadGateway);
    }
})
.WithName("GetHealthAgentTips");

app.MapGet("/api/health-agent-thread/{threadId}", async (string threadId, HttpContext httpContext, FoundryAgentService agentService, CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(threadId))
    {
        return Results.BadRequest(new { error = "threadId is required." });
    }

    try
    {
        var bearerToken = httpContext.Request.Headers.Authorization.ToString();
        var messages = await agentService.GetThreadMessagesAsync(threadId, bearerToken, cancellationToken);
        var response = messages.Select(m => new HealthAgentThreadMessage(m.Role, m.Content, m.RunId, m.CreatedAtUtc));
        return Results.Ok(response);
    }
    catch (InvalidOperationException ex)
    {
        return Results.Problem(
            detail: ex.Message,
            title: "Configuration error",
            statusCode: StatusCodes.Status500InternalServerError);
    }
    catch (HttpRequestException ex)
    {
        return Results.Problem(
            detail: ex.Message,
            title: "Failed to read Foundry thread messages",
            statusCode: StatusCodes.Status502BadGateway);
    }
})
.WithName("GetHealthAgentThread");

app.Run();
