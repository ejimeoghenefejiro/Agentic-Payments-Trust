using AgentTrust.Commerce;

namespace AgentTrust.Api;

public sealed class ConfigurationExternalExecutionControl(IConfiguration configuration) : IExternalExecutionControl
{
    public bool IsAllowed(ServiceActionProposal proposal, out string reason)
    {
        if (!configuration.GetValue("Operations:ExternalExecutionEnabled", false))
            return Deny("EXTERNAL_EXECUTION_DISABLED", out reason);
        if (configuration.GetSection("Operations:BlockedPrincipalIds").Get<string[]>()?.Contains(proposal.PrincipalId, StringComparer.Ordinal) == true)
            return Deny("PRINCIPAL_EXECUTION_BLOCKED", out reason);
        if (configuration.GetSection("Operations:DisabledProviderIds").Get<string[]>()?.Contains(proposal.ProviderId, StringComparer.OrdinalIgnoreCase) == true)
            return Deny("PROVIDER_EXECUTION_DISABLED", out reason);

        var domainSetting = proposal.Action switch
        {
            "place_order" when proposal.ProviderId.Contains("restaurant", StringComparison.OrdinalIgnoreCase) => "RestaurantExecutionEnabled",
            "book_service" => "HomeServiceExecutionEnabled",
            "execute_fulfilment" => "ThirdPartyFulfilmentEnabled",
            _ => "GroceryExecutionEnabled"
        };
        if (!configuration.GetValue($"Operations:{domainSetting}", false))
            return Deny("DOMAIN_EXECUTION_DISABLED", out reason);
        reason = "";
        return true;
    }

    private static bool Deny(string code, out string reason) { reason = code; return false; }
}
