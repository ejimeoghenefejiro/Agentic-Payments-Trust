using AgentTrust.Core;
using AgentTrust.Core.Models;
using Microsoft.AspNetCore.Mvc;

namespace AgentTrust.Api.Controllers;

[ApiController]
[Route("api/bindings")]
public sealed class BindingsController : ControllerBase
{
    private readonly IPrincipalBindingStore _bindings;
    public BindingsController(IPrincipalBindingStore bindings) => _bindings = bindings;

    [HttpPost]
    public IActionResult Create([FromBody] CreateBindingRequest request)
    {
        var binding = new PrincipalBinding(request.AgentId, request.PrincipalId, DateTimeOffset.UtcNow, true, request.BindingEvidenceRef);
        _bindings.Bind(binding);
        return Created(string.Empty, binding);
    }
}
