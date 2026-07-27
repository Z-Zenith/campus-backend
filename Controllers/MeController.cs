using System.Security.Claims;
using BackendApi.Contracts;
using BackendApi.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BackendApi.Controllers;

[ApiController]
[Route("api/v1/me")]
[Authorize]
public class MeController(IPermissionService permissions) : ControllerBase
{
    // Read-only, additive: lets a client (admin-web) decide which nav items/actions to
    // show without probing a mutating endpoint's status code. A flat permission-code
    // list, not the full scoped-permission model (department vs global) — "do they hold
    // it at all" is all UI visibility needs. Enforcement itself stays exactly where it
    // already is, per-endpoint via IPermissionService.HasPermissionAsync — this endpoint
    // only reports what those checks would already say.
    [HttpGet("capabilities")]
    public async Task<ActionResult<MeCapabilitiesResponse>> GetCapabilities()
    {
        var userId = CurrentUserId();
        var held = await permissions.GetEffectivePermissionsAsync(userId, AdminCapabilityPermissions.Codes);
        return Ok(new MeCapabilitiesResponse(held));
    }

    private Guid CurrentUserId() => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub")!);
}
