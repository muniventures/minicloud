using Microsoft.EntityFrameworkCore;
using Minicloud.Api.Auth;
using Minicloud.Common.Contracts;
using Minicloud.Common.Models;
using Minicloud.Common.Services;
using Minicloud.Common.Validation;

namespace Minicloud.Api.Controllers;

public sealed record DeploymentLookup(Deployment? Deployment, IResult? Error);

/// <summary>
/// Resolves customer deployment routes by <c>(organizationId, number)</c> only after authorizing the organization
/// and action, so a number never loads or reveals another tenant's deployment and opaque IDs are not accepted.
/// </summary>
public static class DeploymentNumberResolver
{
    public static async Task<DeploymentLookup> ResolveAsync(
        HttpContext http,
        ControlPlaneAuthorization authorization,
        IQueryable<Deployment> deployments,
        string? organizationId,
        string? deploymentNumber,
        OrganizationAction action,
        CancellationToken ct,
        bool hideUnauthorized = false)
    {
        if (string.IsNullOrWhiteSpace(organizationId))
        {
            var validation = new ValidationResult();
            validation.Add("organizationId", "Organization is required.");
            return new(null, EndpointResults.ValidationProblem("Deployment organization is required.", validation));
        }

        var principal = EndpointResults.RequirePrincipal(http).ToActorIdentity();
        if (!await authorization.CanAccessOrganizationAsync(principal, organizationId, action, ct))
        {
            return new(null, hideUnauthorized ? EndpointResults.DeploymentNotFound() : EndpointResults.Forbidden());
        }

        if (!DeploymentNumber.IsValid(deploymentNumber))
        {
            return new(null, EndpointResults.DeploymentNotFound());
        }

        var deployment = await deployments.SingleOrDefaultAsync(x => x.OrganizationId == organizationId && x.Number == deploymentNumber, ct);
        return deployment is null ? new(null, EndpointResults.DeploymentNotFound()) : new(deployment, null);
    }
}
