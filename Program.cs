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

app.Run();
