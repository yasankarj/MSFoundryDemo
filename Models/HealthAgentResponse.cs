namespace FoundryHealthDemo.Models;

public sealed record HealthAgentResponse(string Type, string Message, string ThreadId, string RawResponse);
