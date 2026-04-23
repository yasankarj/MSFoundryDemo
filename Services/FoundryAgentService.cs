using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace FoundryHealthDemo.Services;

public sealed class FoundryAgentService(HttpClient httpClient, IConfiguration configuration)
{
    private readonly HttpClient _httpClient = httpClient;
    private readonly IConfiguration _configuration = configuration;

    public async Task<string> GetHealthTipAsync(string userMessage, CancellationToken cancellationToken)
    {
        var endpoint = _configuration["Foundry:Endpoint"] ?? _configuration["AZURE_OPENAI_ENDPOINT"];
        var apiKey = _configuration["Foundry:ApiKey"] ?? _configuration["AZURE_OPENAI_API_KEY"];
        var deployment = _configuration["Foundry:Deployment"] ?? _configuration["AZURE_OPENAI_DEPLOYMENT"];
        var apiVersion = _configuration["Foundry:ApiVersion"] ?? _configuration["AZURE_OPENAI_API_VERSION"] ?? "2024-02-15-preview";

        if (string.IsNullOrWhiteSpace(endpoint) ||
            string.IsNullOrWhiteSpace(apiKey) ||
            string.IsNullOrWhiteSpace(deployment))
        {
            throw new InvalidOperationException(
                "Missing Foundry settings. Provide Foundry:Endpoint, Foundry:ApiKey, Foundry:Deployment in appsettings, or AZURE_OPENAI_ENDPOINT, AZURE_OPENAI_API_KEY, AZURE_OPENAI_DEPLOYMENT as environment variables.");
        }

        var url = $"{endpoint.TrimEnd('/')}/openai/deployments/{deployment}/chat/completions?api-version={apiVersion}";

        var payload = new
        {
            messages = new object[]
            {
                new { role = "system", content = "You are a helpful health and wellness assistant. Give safe, practical, non-diagnostic health tips. Keep responses concise and include a short disclaimer that this is not medical advice. If the user mentions a health condition, do not diagnose it. Just provide general advice." },
                new { role = "system", content = "If user asks questions not related to health, politely decline and ask them to focus on health topics." },
                new { role = "user", content = userMessage }
            },
            temperature = 0.7,
            max_tokens = 300
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Add("api-key", apiKey);
        request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"Foundry endpoint returned {(int)response.StatusCode}: {responseBody}");
        }

        using var doc = JsonDocument.Parse(responseBody);
        var content = doc.RootElement
            .GetProperty("choices")[0]
            .GetProperty("message")
            .GetProperty("content")
            .GetString();

        return string.IsNullOrWhiteSpace(content)
            ? "No response was returned by the model."
            : content.Trim();
    }
}
