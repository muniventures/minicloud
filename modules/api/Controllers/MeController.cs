using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Minicloud.Api.Auth;
using Minicloud.Common.Data;
using Minicloud.Common.Contracts;
using Minicloud.Common.Models;
using Minicloud.Api.Providers;
using Minicloud.Api.Services;
using Minicloud.Common.Services;
using Minicloud.Common.Validation;
using static Minicloud.Api.Controllers.EndpointResults;

namespace Minicloud.Api.Controllers;

[ApiController]
[Authorize]
[Route("v1/[controller]")]
public sealed class MeController : ControllerBase
{
    [HttpGet]
    public async Task<IResult> GetMe([FromServices] MinicloudDbContext db, CancellationToken cancellationToken)
    {
        var principal = RequirePrincipal(HttpContext);
        if (principal.IsApiKey && principal.ApiKeyId is not null && principal.OrganizationId is not null)
        {
            var organization = await db.Organizations.SingleOrDefaultAsync(x => x.Id == principal.OrganizationId, cancellationToken);
            if (organization is null)
            {
                return EndpointResults.NotFound("organization_not_found", "Organization was not found.");
            }

            return Results.Ok(new MeResponse(
                principal.ApiKeyId,
                "api-key",
                "API key",
                [new OrganizationSummaryDto(organization.Id, organization.Name, organization.Slug, "api_key")]));
        }

        if (!principal.IsDashboardUser || principal.UserId is null)
        {
            return EndpointResults.Forbidden();
        }

        var user = await db.Users
            .Include(x => x.Memberships)
            .ThenInclude(x => x.Organization)
            .SingleAsync(x => x.Id == principal.UserId, cancellationToken);

        return Results.Ok(new MeResponse(
            user.Id,
            user.Email,
            user.DisplayName,
            user.Memberships.Select(x => new OrganizationSummaryDto(x.Organization.Id, x.Organization.Name, x.Organization.Slug, ControlPlaneValidation.ToWireValue(x.Role))).ToArray()));
    }
}
