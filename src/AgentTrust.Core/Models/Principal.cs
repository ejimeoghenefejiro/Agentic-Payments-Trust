namespace AgentTrust.Core.Models;

public sealed record Principal(string PrincipalId, string Name, DateTimeOffset RegisteredAt);

public sealed record Merchant(string MerchantId, string Name, string Category, bool Approved);
