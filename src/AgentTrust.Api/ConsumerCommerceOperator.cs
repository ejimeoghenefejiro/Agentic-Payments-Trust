using AgentTrust.Commerce;
using AgentTrust.Consumer;

namespace AgentTrust.Api;

/// <summary>
/// The only agent worker allowed to cross into execution. It accepts no free-form model output:
/// the proposal must be auditor-accepted and already pass deterministic readiness checks.
/// </summary>
public sealed class ConsumerCommerceOperator
{
    private readonly IConsumerTaskStore _tasks;private readonly AgentPurchaseOrchestrator _orchestrator;
    private readonly ICommerceConnector _connector;private readonly IConfiguration _configuration;
    public ConsumerCommerceOperator(IConsumerTaskStore tasks,AgentPurchaseOrchestrator orchestrator,
        MerchantConnectorRegistry connectors,IConfiguration configuration)
    {_tasks=tasks;_orchestrator=orchestrator;_connector=connectors.All.Single();_configuration=configuration;}

    public async Task<PurchaseOrchestrationResult> ExecuteAsync(ConsumerCommercePreparation preparation,
        ConsumerPurchaseTask preparedTask,string principalId,CancellationToken cancellationToken)
    {
        if(!preparation.IsExecutable)throw new InvalidOperationException("OPERATOR_READINESS_REQUIRED");
        if(!preparation.Plan.ToolsUsed.Contains("auditor:accepted",StringComparer.OrdinalIgnoreCase)&&AgentTrust.Agents.AgentFactory.IsLiveModeConfigured)
            throw new InvalidOperationException("OPERATOR_AUDIT_ACCEPTANCE_REQUIRED");
        var task=_tasks.FindOwned(preparedTask.TaskId,principalId);
        if(task is null){task=preparedTask;_tasks.Save(task);}
        var stripe=string.Equals(_configuration["Payments:Provider"],"Stripe",StringComparison.OrdinalIgnoreCase);
        return await _orchestrator.RunAsync(task.TaskId,principalId,task.CreatedAt,_connector,new(stripe,true),cancellationToken);
    }
}
