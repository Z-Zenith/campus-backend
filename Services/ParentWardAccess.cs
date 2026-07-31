using System.Security.Claims;
using BackendApi.Data;
using BackendApi.Data.Entities;

namespace BackendApi.Services;

// PRT-02/03: shared gate for the parent-scoped ward endpoints. A parent's JWT is bound to
// exactly one ward at login (see JwtTokenService); every read/write against ward data must
// check that binding against the requested studentId here, never trust the route alone.
public static class ParentWardAccess
{
    public static async Task<Guid?> GetAuthorizedParentIdAsync(
        AppDbContext db, IAppAuthorizationService permissions, ClaimsPrincipal principal, Guid requestedStudentId)
    {
        if (principal.FindFirstValue("account_type") != nameof(AccountType.Parent))
        {
            return null;
        }

        var wardIdClaim = principal.FindFirstValue("ward_id");
        if (wardIdClaim is null || !Guid.TryParse(wardIdClaim, out var wardId) || wardId != requestedStudentId)
        {
            return null;
        }

        var sessionIdClaim = principal.FindFirstValue("session_id");
        var userIdClaim = principal.FindFirstValue(ClaimTypes.NameIdentifier) ?? principal.FindFirstValue("sub");
        if (sessionIdClaim is null || userIdClaim is null ||
            !Guid.TryParse(sessionIdClaim, out var sessionId) || !Guid.TryParse(userIdClaim, out var userId))
        {
            return null;
        }

        var session = await db.UserSessions.FindAsync(sessionId);
        if (session is null || !session.IsActive || session.UserId != userId)
        {
            return null;
        }

        // Re-check the live parent_wards link rather than trusting the JWT's ward_id claim
        // alone, so revoking a parent's access to a ward takes effect immediately instead of
        // only after the token expires.
        if (!await permissions.CheckRelationAsync(userId, "parent", "student", requestedStudentId.ToString()))
        {
            return null;
        }

        var parent = await db.Users.FindAsync(userId);
        if (parent is null || !parent.IsActive)
        {
            return null;
        }

        return userId;
    }
}
