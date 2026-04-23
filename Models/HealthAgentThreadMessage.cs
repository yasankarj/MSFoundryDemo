namespace FoundryHealthDemo.Models;

public sealed record HealthAgentThreadMessage(string Role, string Content, string? RunId, DateTimeOffset? CreatedAtUtc);
