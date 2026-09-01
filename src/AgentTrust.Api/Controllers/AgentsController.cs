using AgentTrust.Core;
using AgentTrust.Core.Models;
using Microsoft.AspNetCore.Mvc;

namespace AgentTrust.Api.Controllers;

[ApiController]
[Route("api/agents")]
public sealed class AgentsController : ControllerBase
{
    private readonly IAgentRegistry _agents;
    public AgentsController(IAgentRegistry agents) => _agents = agents;

    [HttpPost]
    public IActionResult Register([FromBody] RegisterAgentRequest request)
    {
        var now = DateTimeOffset.UtcNow;
        var identity = new AgentIdentity(
            request.AgentId, request.PrincipalId, request.AgentType, request.Environment,
            CredentialStatus.Active, request.IssuedAt ?? now, request.ExpiresAt ?? now.AddYears(1), request.Issuer);
        _agents.Register(identity);
        return CreatedAtAction(nameof(Get), new { id = identity.AgentId }, identity);
    }

    [HttpGet("{id}")]
    public IActionResult Get(string id)
    {
        var agent = _agents.Find(id);
        return agent is null ? NotFound() : Ok(agent);
    }

    [HttpPost("{id}/revoke")]
    public IActionResult Revoke(string id)
    {
        var agent = _agents.Find(id);
        if (agent is null) return NotFound();
        _agents.Register(agent with { CredentialStatus = CredentialStatus.Revoked });
        return Ok(_agents.Find(id));
    }
}
