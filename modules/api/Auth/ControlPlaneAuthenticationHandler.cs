using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Minicloud.Api.Auth;

public sealed class ControlPlaneAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    ControlPlanePrincipalResolver principalResolver)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "ControlPlane";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (Request.Path.StartsWithSegments("/v2") || Request.Path == "/v1/registry/token")
        {
            return AuthenticateResult.NoResult();
        }
        var authorization = Request.Headers.Authorization.ToString();
        if (string.IsNullOrWhiteSpace(authorization) || !authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return AuthenticateResult.NoResult();
        }

        var token = authorization["Bearer ".Length..].Trim();
        var principal = await principalResolver.ResolveAsync(token, Context.RequestAborted);
        if (principal is null)
        {
            return AuthenticateResult.Fail("Invalid bearer token.");
        }

        var claims = new List<Claim> { new("auth_kind", principal.Kind) };
        if (principal.UserId is not null)
        {
            claims.Add(new Claim(ClaimTypes.NameIdentifier, principal.UserId));
        }
        if (principal.ApiKeyId is not null)
        {
            claims.Add(new Claim("api_key_id", principal.ApiKeyId));
        }
        if (principal.OrganizationId is not null)
        {
            claims.Add(new Claim("organization_id", principal.OrganizationId));
        }
        claims.AddRange(principal.Scopes.Select(scope => new Claim("scope", scope)));

        var identity = new ClaimsIdentity(claims, SchemeName);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName));
    }
}
