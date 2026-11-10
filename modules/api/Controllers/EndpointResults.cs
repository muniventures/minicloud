using Microsoft.EntityFrameworkCore;
using Minicloud.Api.Auth;
using Minicloud.Common.Contracts;
using Minicloud.Common.Data;
using Minicloud.Common.Services;

namespace Minicloud.Api.Controllers;

public static class EndpointResults
{
    public static ControlPlanePrincipal RequirePrincipal(HttpContext http) =>
        http.User.GetControlPlanePrincipal() ?? throw new InvalidOperationException("Authenticated control-plane principal was not resolved.");

    public static IResult ValidationProblem(string message, Minicloud.Common.Validation.ValidationResult validation) =>
        Results.BadRequest(new ErrorResponse(new ErrorBody("validation_failed", message, validation.Errors)));

    public static IResult Forbidden() =>
        Results.Json(new ErrorResponse(new ErrorBody("forbidden", "You are not allowed to access this resource.", [])), statusCode: StatusCodes.Status403Forbidden);

    public static IResult NotFound(string code, string message) =>
        Results.NotFound(new ErrorResponse(new ErrorBody(code, message, [])));

    public static IResult Conflict(string code, string message) =>
        Results.Conflict(new ErrorResponse(new ErrorBody(code, message, [])));

    public static IResult Problem(string code, string message, int statusCode) =>
        Results.Json(new ErrorResponse(new ErrorBody(code, message, [])), statusCode: statusCode);

    public static async Task<IResult?> VerifyTargetOwnershipAsync(
        MinicloudDbContext db,
        string? appId,
        string? applicationId,
        string? environmentId,
        CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(environmentId) &&
            !string.IsNullOrWhiteSpace(appId) &&
            !string.Equals(environmentId, appId, StringComparison.Ordinal))
        {
            return Conflict("target_mismatch", "Environment identity does not match the specified appId.");
        }

        var targetId = !string.IsNullOrWhiteSpace(environmentId) ? environmentId : appId;
        if (string.IsNullOrWhiteSpace(targetId))
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(applicationId))
        {
            var env = await db.Apps.AsNoTracking().SingleOrDefaultAsync(x => x.Id == targetId, ct);
            if (env is null)
            {
                return NotFound("app_not_found", "App was not found.");
            }
            if (!string.Equals(env.ApplicationId, applicationId, StringComparison.Ordinal))
            {
                return Conflict("target_mismatch", "Environment does not belong to the specified application.");
            }
        }

        return null;
    }
}
