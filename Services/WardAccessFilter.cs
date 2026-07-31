using BackendApi.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace BackendApi.Services;

// Applies the ParentWardAccess gate to any [Authorize] action whose route has a `studentId`
// parameter, so a new ward-scoped endpoint is denied by default instead of depending on
// remembering to paste the check manually into the action body.
public class WardAccessFilter(AppDbContext db, IAppAuthorizationService permissions) : IAsyncActionFilter
{
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        if (!context.RouteData.Values.TryGetValue("studentId", out var raw) ||
            raw is not string raw2 || !Guid.TryParse(raw2, out var studentId))
        {
            context.Result = new BadRequestResult();
            return;
        }

        if (await ParentWardAccess.GetAuthorizedParentIdAsync(db, permissions, context.HttpContext.User, studentId) is null)
        {
            // #93: NotFound rather than Forbid — a caller who isn't authorized for this ward
            // must not be able to distinguish "this student doesn't exist" from "it's not your
            // ward" by the status code alone. Matches the standardized 404-for-both convention
            // for ward-scoped resources used elsewhere (e.g. BrowsingController.
            // ApproveWhitelistRequest's college-scoping check).
            context.Result = new NotFoundResult();
            return;
        }

        await next();
    }
}
