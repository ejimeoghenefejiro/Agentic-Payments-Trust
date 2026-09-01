using AgentTrust.Core;
using AgentTrust.Core.Models;
using Microsoft.AspNetCore.Mvc;

namespace AgentTrust.Api.Controllers;

[ApiController]
[Route("api/principals")]
public sealed class PrincipalsController : ControllerBase
{
    private readonly IPrincipalStore _principals;
    public PrincipalsController(IPrincipalStore principals) => _principals = principals;

    [HttpPost]
    public IActionResult Register([FromBody] RegisterPrincipalRequest request)
    {
        var principal = new Principal(request.PrincipalId, request.Name, DateTimeOffset.UtcNow);
        _principals.Register(principal);
        return CreatedAtAction(nameof(Register), new { id = principal.PrincipalId }, principal);
    }
}
