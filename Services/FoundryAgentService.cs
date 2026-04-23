using Azure.Core;
using Azure.Identity;
using Microsoft.Identity.Client;
using Microsoft.Extensions.Logging;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace FoundryHealthDemo.Services;

public sealed class FoundryAgentService(HttpClient httpClient, IConfiguration configuration, ILogger<FoundryAgentService> logger)
{
    private readonly HttpClient _httpClient = httpClient;
    private readonly IConfiguration _configuration = configuration;
    private readonly ILogger<FoundryAgentService> _logger = logger;
    private static readonly TokenCredential AgentCredential = new DefaultAzureCredential();

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

    public async Task<(string Response, string ThreadId)> GetHealthTipFromAgentAsync(
        string userMessage,
        string? threadId,
        string? incomingBearerToken,
        CancellationToken cancellationToken)
    {
        var endpoint = _configuration["Foundry:ProjectEndpoint"] ??
                       _configuration["Foundry:Endpoint"] ??
                       _configuration["AZURE_OPENAI_ENDPOINT"];
        var agentId = _configuration["Foundry:HealthAgentID"] ?? _configuration["AZURE_FOUNDRY_AGENT_ID"];
        var apiVersion = _configuration["Foundry:AgentApiVersion"] ??
                         _configuration["Foundry:ApiVersion"] ??
                         "v1";
        var agentAuthScope = _configuration["Foundry:AgentAuthScope"] ?? "https://ai.azure.com/.default";

        if (string.IsNullOrWhiteSpace(endpoint) ||
            string.IsNullOrWhiteSpace(agentId))
        {
            throw new InvalidOperationException(
                "Missing Foundry agent settings. Provide Foundry:ProjectEndpoint (or Foundry:Endpoint) and Foundry:HealthAgentID in appsettings, or AZURE_OPENAI_ENDPOINT and AZURE_FOUNDRY_AGENT_ID as environment variables.");
        }

        var (accessToken, authSource) = await GetAgentAccessTokenAsync(agentAuthScope, incomingBearerToken, cancellationToken);
        LogTokenDiagnostics(accessToken, authSource, agentAuthScope);
        var normalizedEndpoint = endpoint.TrimEnd('/');
        var isProjectEndpoint = normalizedEndpoint.Contains("/api/projects/", StringComparison.OrdinalIgnoreCase);
        var baseUrl = isProjectEndpoint
            ? normalizedEndpoint
            : $"{normalizedEndpoint}/openai";
        var activeThreadId = string.IsNullOrWhiteSpace(threadId)
            ? await CreateThreadAsync(baseUrl, apiVersion, accessToken, cancellationToken)
            : threadId;

        await AddMessageAsync(baseUrl, apiVersion, accessToken, activeThreadId!, userMessage, cancellationToken);
        var runId = await CreateRunAsync(baseUrl, apiVersion, accessToken, activeThreadId!, agentId, cancellationToken);
        await WaitForRunCompletionAsync(baseUrl, apiVersion, accessToken, activeThreadId!, runId, cancellationToken);
        var responseText = await GetLatestAssistantMessageAsync(baseUrl, apiVersion, accessToken, activeThreadId!, cancellationToken);

        return (responseText, activeThreadId!);
    }

    private async Task<(string Token, string Source)> GetAgentAccessTokenAsync(string scope, string? incomingBearerToken, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(incomingBearerToken))
        {
            var normalizedIncomingToken = NormalizeBearerToken(incomingBearerToken);
            if (!string.IsNullOrWhiteSpace(normalizedIncomingToken))
            {
                if (TryReadJwtClaims(normalizedIncomingToken, out var incomingAud, out _, out _) &&
                    string.Equals(incomingAud, "https://ai.azure.com", StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogInformation("Foundry auth: incoming bearer token already targets https://ai.azure.com. Using pass-through token.");
                    return (normalizedIncomingToken, "incoming-pass-through-ai-audience");
                }

                var oboToken = await TryGetOnBehalfOfTokenAsync(normalizedIncomingToken, scope, cancellationToken);
                if (!string.IsNullOrWhiteSpace(oboToken))
                {
                    return (oboToken, "obo");
                }

                _logger.LogWarning(
                    "Foundry auth: incoming token audience is not https://ai.azure.com and OBO failed. Falling back to configured/default credential token acquisition.");
            }
        }

        var configuredToken =
            _configuration["Foundry:AgentAccessToken"] ??
            _configuration["AZURE_FOUNDRY_AGENT_ACCESS_TOKEN"] ??
            _configuration["AzureAd:AccessToken"];

        if (!string.IsNullOrWhiteSpace(configuredToken))
        {
            return (NormalizeBearerToken(configuredToken), "configured-token");
        }

        try
        {
            var token = await AgentCredential.GetTokenAsync(
                new TokenRequestContext(new[] { scope }),
                cancellationToken);
            return (token.Token, "default-azure-credential");
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Failed to acquire Azure AD token for Foundry agent calls. Scope: {scope}. Ensure you are signed in (for example via Azure CLI/Visual Studio) and have project/workspace permissions.",
                ex);
        }
    }

    private async Task<string?> TryGetOnBehalfOfTokenAsync(
        string incomingAccessToken,
        string scope,
        CancellationToken cancellationToken)
    {
        var tenantId = _configuration["AzureAd:TenantId"];
        var clientId = _configuration["AzureAd:ClientId"];
        var clientSecret = _configuration["AzureAd:ClientSecret"];
        var authorityBase = _configuration["AzureAd:Instance"] ?? "https://login.microsoftonline.com/";

        if (string.IsNullOrWhiteSpace(tenantId) ||
            string.IsNullOrWhiteSpace(clientId) ||
            string.IsNullOrWhiteSpace(clientSecret))
        {
            return null;
        }

        try
        {
            var authority = $"{authorityBase.TrimEnd('/')}/{tenantId}";
            var app = ConfidentialClientApplicationBuilder
                .Create(clientId)
                .WithClientSecret(clientSecret)
                .WithAuthority(authority)
                .Build();

            var result = await app
                .AcquireTokenOnBehalfOf(new[] { scope }, new UserAssertion(incomingAccessToken))
                .ExecuteAsync(cancellationToken);

            _logger.LogInformation("Foundry auth: OBO token exchange succeeded for scope {Scope}.", scope);
            return result.AccessToken;
        }
        catch (MsalServiceException ex)
        {
            _logger.LogWarning(
                "Foundry auth: OBO token exchange failed (code: {ErrorCode}). Falling back to pass-through token.",
                ex.ErrorCode);
            return null;
        }
    }

    private static string NormalizeBearerToken(string token)
    {
        const string bearerPrefix = "Bearer ";
        var trimmed = token.Trim();
        if (trimmed.StartsWith(bearerPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return trimmed[bearerPrefix.Length..].Trim();
        }

        return trimmed;
    }

    private void LogTokenDiagnostics(string token, string authSource, string scope)
    {
        if (!TryReadJwtClaims(token, out var aud, out var tid, out var expUtc))
        {
            _logger.LogWarning(
                "Foundry auth source {AuthSource}. Token could not be parsed as JWT. Scope requested: {Scope}.",
                authSource,
                scope);
            return;
        }

        _logger.LogInformation(
            "Foundry auth source {AuthSource}. Token claims -> aud: {Audience}, tid: {TenantId}, expUtc: {ExpiryUtc}, scope: {Scope}.",
            authSource,
            aud ?? "<missing>",
            tid ?? "<missing>",
            expUtc?.ToString("O") ?? "<missing>",
            scope);
    }

    private static bool TryReadJwtClaims(string token, out string? aud, out string? tid, out DateTimeOffset? expUtc)
    {
        aud = null;
        tid = null;
        expUtc = null;

        var parts = token.Split('.');
        if (parts.Length < 2)
        {
            return false;
        }

        try
        {
            var payloadBytes = DecodeBase64Url(parts[1]);
            using var payloadDoc = JsonDocument.Parse(payloadBytes);
            var root = payloadDoc.RootElement;

            if (root.TryGetProperty("aud", out var audElement))
            {
                aud = audElement.GetString();
            }

            if (root.TryGetProperty("tid", out var tidElement))
            {
                tid = tidElement.GetString();
            }

            if (root.TryGetProperty("exp", out var expElement) && expElement.TryGetInt64(out var expSeconds))
            {
                expUtc = DateTimeOffset.FromUnixTimeSeconds(expSeconds).ToUniversalTime();
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    private static byte[] DecodeBase64Url(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        padded = padded.PadRight(padded.Length + ((4 - padded.Length % 4) % 4), '=');
        return Convert.FromBase64String(padded);
    }

    private async Task<string> CreateThreadAsync(
        string baseUrl,
        string apiVersion,
        string accessToken,
        CancellationToken cancellationToken)
    {
        var url = $"{baseUrl}/threads?api-version={apiVersion}";
        using var request = CreateAgentRequest(HttpMethod.Post, url, accessToken);
        request.Content = new StringContent("{}", Encoding.UTF8, "application/json");

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"Foundry endpoint returned {(int)response.StatusCode}: {responseBody}");
        }

        using var doc = JsonDocument.Parse(responseBody);
        return doc.RootElement.GetProperty("id").GetString()
               ?? throw new HttpRequestException("Thread creation succeeded but no thread id was returned.");
    }

    private async Task AddMessageAsync(
        string baseUrl,
        string apiVersion,
        string accessToken,
        string threadId,
        string userMessage,
        CancellationToken cancellationToken)
    {
        var url = $"{baseUrl}/threads/{threadId}/messages?api-version={apiVersion}";
        var payload = JsonSerializer.Serialize(new
        {
            role = "user",
            content = userMessage
        });

        using var request = CreateAgentRequest(HttpMethod.Post, url, accessToken);
        request.Content = new StringContent(payload, Encoding.UTF8, "application/json");

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"Foundry endpoint returned {(int)response.StatusCode}: {responseBody}");
        }
    }

    private async Task<string> CreateRunAsync(
        string baseUrl,
        string apiVersion,
        string accessToken,
        string threadId,
        string agentId,
        CancellationToken cancellationToken)
    {
        var url = $"{baseUrl}/threads/{threadId}/runs?api-version={apiVersion}";
        var payload = JsonSerializer.Serialize(new { assistant_id = agentId });

        using var request = CreateAgentRequest(HttpMethod.Post, url, accessToken);
        request.Content = new StringContent(payload, Encoding.UTF8, "application/json");

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"Foundry endpoint returned {(int)response.StatusCode}: {responseBody}");
        }

        using var doc = JsonDocument.Parse(responseBody);
        return doc.RootElement.GetProperty("id").GetString()
               ?? throw new HttpRequestException("Run creation succeeded but no run id was returned.");
    }

    private async Task WaitForRunCompletionAsync(
        string baseUrl,
        string apiVersion,
        string accessToken,
        string threadId,
        string runId,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            var url = $"{baseUrl}/threads/{threadId}/runs/{runId}?api-version={apiVersion}";
            using var request = CreateAgentRequest(HttpMethod.Get, url, accessToken);

            using var response = await _httpClient.SendAsync(request, cancellationToken);
            var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException(
                    $"Foundry endpoint returned {(int)response.StatusCode}: {responseBody}");
            }

            using var doc = JsonDocument.Parse(responseBody);
            var status = doc.RootElement.GetProperty("status").GetString();
            if (string.Equals(status, "completed", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(status, "cancelled", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(status, "expired", StringComparison.OrdinalIgnoreCase))
            {
                throw new HttpRequestException($"Foundry run did not complete successfully. Status: {status}");
            }

            await Task.Delay(700, cancellationToken);
        }
    }

    private async Task<string> GetLatestAssistantMessageAsync(
        string baseUrl,
        string apiVersion,
        string accessToken,
        string threadId,
        CancellationToken cancellationToken)
    {
        var url = $"{baseUrl}/threads/{threadId}/messages?api-version={apiVersion}&order=desc&limit=20";
        using var request = CreateAgentRequest(HttpMethod.Get, url, accessToken);

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"Foundry endpoint returned {(int)response.StatusCode}: {responseBody}");
        }

        using var doc = JsonDocument.Parse(responseBody);
        if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
        {
            return "No response was returned by the agent.";
        }

        foreach (var message in data.EnumerateArray())
        {
            var role = message.GetProperty("role").GetString();
            if (!string.Equals(role, "assistant", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!message.TryGetProperty("content", out var contentItems) || contentItems.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var item in contentItems.EnumerateArray())
            {
                if (!item.TryGetProperty("type", out var typeElement) ||
                    !string.Equals(typeElement.GetString(), "text", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!item.TryGetProperty("text", out var textElement) ||
                    !textElement.TryGetProperty("value", out var valueElement))
                {
                    continue;
                }

                var value = valueElement.GetString();
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value.Trim();
                }
            }
        }

        return "No response was returned by the agent.";
    }

    private static HttpRequestMessage CreateAgentRequest(HttpMethod method, string url, string accessToken)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return request;
    }
}
