using AgentTrust.Core;
using AgentTrust.Core.Models;
using Microsoft.AspNetCore.Mvc;

namespace AgentTrust.Api.Controllers;

[ApiController]
[Route("api/authorities")]
public sealed class AuthoritiesController : ControllerBase
{
    private readonly IDelegatedAuthorityStore _authorities;
    public AuthoritiesController(IDelegatedAuthorityStore authorities) => _authorities = authorities;

    [HttpPost]
    public IActionResult Grant([FromBody] GrantAuthorityRequest request)
    {
        var authority = new DelegatedAuthority(
            request.AuthorityId, request.AgentId, request.Permissions, request.PerTransactionLimit,
            request.DailyLimit, request.ApprovedMerchants, request.CategoryScope, request.GeographicScope,
            null, null, request.HumanApprovalAbove, request.Expiry, false);
        _authorities.Grant(authority);
        return CreatedAtAction(nameof(Get), new { id = authority.AuthorityId }, authority);
    }

    [HttpGet("{id}")]
    public IActionResult Get(string id)
    {
        var authority = _authorities.FindById(id);
        return authority is null ? NotFound() : Ok(authority);
    }

    [HttpPost("{id}/revoke")]
    public IActionResult Revoke(string id)
    {
        if (_authorities.FindById(id) is null) return NotFound();
        _authorities.Revoke(id);
        return Ok(_authorities.FindById(id));
    }
}
