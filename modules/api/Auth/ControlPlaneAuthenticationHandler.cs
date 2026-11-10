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
        if (Request.Path.StartsWithSegments("/v2") || Request.Path == "/api/registry/token")
        {
            return AuthenticateResult.NoResult();
        }
        var authorization = Request.Headers.Authorization.ToString();
        if (!string.IsNullOrWhiteSpace(authorization))
        {
            if (!authorization.StartsWith("Bearer", StringComparison.OrdinalIgnoreCase))
            {
                return AuthenticateResult.NoResult();
            }

            if (authorization.Length <= "Bearer".Length ||
                !char.IsWhiteSpace(authorization["Bearer".Length]))
            {
                return AuthenticateResult.Fail("Invalid bearer token.");
            }

            var token = authorization["Bearer".Length..].Trim();
            if (string.IsNullOrWhiteSpace(token))
            {
                return AuthenticateResult.Fail("Invalid bearer token.");
            }

            var bearerPrincipal = await principalResolver.ResolveAsync(token, Context.RequestAborted);
            return bearerPrincipal is null
                ? AuthenticateResult.Fail("Invalid bearer token.")
                : AuthenticateResult.Success(CreateTicket(bearerPrincipal));
        }

        var cookie = Request.Cookies["__Host-minicloud-session"];
        if (string.IsNullOrWhiteSpace(cookie))
        {
            return AuthenticateResult.NoResult();
        }

        var principal = await principalResolver.ResolveBrowserSessionAsync(cookie, Context.RequestAborted);
        if (principal is null)
        {
            return AuthenticateResult.NoResult();
        }

        return AuthenticateResult.Success(CreateTicket(principal));
    }

    private static AuthenticationTicket CreateTicket(ControlPlanePrincipal principal)
    {
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
        if (principal.BrowserSessionId is not null)
        {
            claims.Add(new Claim("browser_session_id", principal.BrowserSessionId));
        }
        claims.AddRange(principal.Scopes.Select(scope => new Claim("scope", scope)));

        var identity = new ClaimsIdentity(claims, ControlPlaneAuthenticationHandler.SchemeName);
        return new AuthenticationTicket(new ClaimsPrincipal(identity), ControlPlaneAuthenticationHandler.SchemeName);
    }
}
