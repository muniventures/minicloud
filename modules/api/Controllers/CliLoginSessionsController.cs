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
[Route("v1/[controller]")]
public sealed class CliLoginSessionsController : ControllerBase
{
    [HttpPost]
    [AllowAnonymous]
    public async Task<IResult> CreateCliLoginSession([FromServices] MinicloudDbContext db, [FromServices] IIdGenerator ids, [FromServices] TimeProvider timeProvider, [FromServices] IConsoleUrlProvider consoleUrls, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var session = new CliLoginSession
        {
            Id = ids.NewId("cli"),
            Status = "pending",
            ScopesJson = JsonSerializer.Serialize(ApiKeyScopes.CliLoginDefault),
            CreatedAt = now,
            ExpiresAt = now.AddMinutes(10)
        };
        db.CliLoginSessions.Add(session);
        await db.SaveChangesAsync(cancellationToken);

        var consoleBaseUrl = consoleUrls.GetConsoleBaseUrl();
        var loginUrl = $"{consoleBaseUrl}/cli/login?session={Uri.EscapeDataString(session.Id)}";
        return Results.Created($"/v1/cli-login-sessions/{session.Id}", new CliLoginSessionCreateResponse(session.Id, loginUrl, session.Status, session.ExpiresAt));
    }

    [HttpGet("{sessionId}")]
    [AllowAnonymous]
    public async Task<IResult> GetCliLoginSession([FromRoute] string sessionId, [FromServices] MinicloudDbContext db, [FromServices] TimeProvider timeProvider, CancellationToken cancellationToken)
    {
        var session = await db.CliLoginSessions.AsNoTracking().SingleOrDefaultAsync(x => x.Id == sessionId, cancellationToken);
        return session is null
            ? EndpointResults.NotFound("cli_login_session_not_found", "CLI login session was not found.")
            : Results.Ok(ToCliLoginSessionResponse(session, timeProvider.GetUtcNow()));
    }

    [HttpPost("{sessionId}/exchange")]
    [AllowAnonymous]
    public async Task<IResult> ExchangeCliLoginSession([FromRoute] string sessionId, [FromServices] MinicloudDbContext db, [FromServices] TimeProvider timeProvider, CancellationToken cancellationToken)
    {
        var session = await db.CliLoginSessions
            .Include(x => x.Organization)
            .Include(x => x.ApprovedByUser)
            .SingleOrDefaultAsync(x => x.Id == sessionId, cancellationToken);
        if (session is null)
        {
            return EndpointResults.NotFound("cli_login_session_not_found", "CLI login session was not found.");
        }

        var now = timeProvider.GetUtcNow();
        if (session.ExpiresAt <= now)
        {
            session.Status = "expired";
            session.RawApiKeyToken = null;
            await db.SaveChangesAsync(cancellationToken);
            return Results.BadRequest(new ErrorResponse(new ErrorBody("cli_login_session_expired", "CLI login session has expired.", [])));
        }

        if (session.Status == "exchanged" || session.ExchangedAt is not null)
        {
            return Results.BadRequest(new ErrorResponse(new ErrorBody("cli_login_session_exchanged", "CLI login session has already been exchanged.", [])));
        }

        if (session.Status != "approved" || string.IsNullOrWhiteSpace(session.RawApiKeyToken) || session.Organization is null || session.ApprovedByUser is null)
        {
            return Results.Accepted($"/v1/cli-login-sessions/{session.Id}", ToCliLoginSessionResponse(session, now));
        }

        var token = session.RawApiKeyToken;
        session.RawApiKeyToken = null;
        session.Status = "exchanged";
        session.ExchangedAt = now;
        await db.SaveChangesAsync(cancellationToken);

        var scopes = JsonSerializer.Deserialize<string[]>(session.ScopesJson) ?? [];
        return Results.Ok(new CliLoginSessionExchangeResponse(
            token,
            session.ApprovedByUser.Email,
            new OrganizationSummaryDto(session.Organization.Id, session.Organization.Name, session.Organization.Slug, "owner"),
            scopes));
    }

    [HttpPost("{sessionId}/approve")]
    [Authorize]
    public async Task<IResult> ApproveCliLoginSession([FromRoute] string sessionId, [FromBody] ApproveCliLoginSessionRequest request, [FromServices] MinicloudDbContext db, [FromServices] ControlPlaneAuthorization authorization, [FromServices] IIdGenerator ids, [FromServices] IApiKeyTokenService tokenService, [FromServices] TimeProvider timeProvider, CancellationToken cancellationToken)
    {
        var principal = RequirePrincipal(HttpContext);
        if (!principal.IsDashboardUser || principal.UserId is null)
        {
            return EndpointResults.Forbidden();
        }

        if (!await authorization.CanAccessOrganizationAsync(principal.ToActorIdentity(), request.OrganizationId, OrganizationAction.WriteApiKeys, cancellationToken))
        {
            return EndpointResults.Forbidden();
        }

        var session = await db.CliLoginSessions.SingleOrDefaultAsync(x => x.Id == sessionId, cancellationToken);
        if (session is null)
        {
            return EndpointResults.NotFound("cli_login_session_not_found", "CLI login session was not found.");
        }

        var now = timeProvider.GetUtcNow();
        if (session.ExpiresAt <= now)
        {
            session.Status = "expired";
            await db.SaveChangesAsync(cancellationToken);
            return Results.BadRequest(new ErrorResponse(new ErrorBody("cli_login_session_expired", "CLI login session has expired.", [])));
        }

        if (session.Status != "pending")
        {
            return Results.BadRequest(new ErrorResponse(new ErrorBody("cli_login_session_not_pending", "CLI login session can no longer be approved.", [])));
        }

        var token = tokenService.Generate();
        var scopes = JsonSerializer.Deserialize<string[]>(session.ScopesJson) ?? [];
        var key = new ApiKey
        {
            Id = ids.NewId("key"),
            OrganizationId = request.OrganizationId,
            Name = $"CLI on {Environment.MachineName}",
            KeyPrefix = token.Prefix,
            KeyHash = tokenService.Hash(token.Token),
            ScopesJson = JsonSerializer.Serialize(scopes.Distinct(StringComparer.Ordinal).Order().ToArray()),
            CreatedAt = now,
            UpdatedAt = now
        };
        db.ApiKeys.Add(key);

        session.OrganizationId = request.OrganizationId;
        session.ApprovedByUserId = principal.UserId;
        session.ApiKeyId = key.Id;
        session.RawApiKeyToken = token.Token;
        session.Status = "approved";
        session.ApprovedAt = now;
        await db.SaveChangesAsync(cancellationToken);

        return Results.Ok(ToCliLoginSessionResponse(session, now));
    }


    static CliLoginSessionResponse ToCliLoginSessionResponse(CliLoginSession session, DateTimeOffset now)
    {
        var status = session.ExpiresAt <= now && session.Status is "pending" or "approved" ? "expired" : session.Status;
        var scopes = JsonSerializer.Deserialize<string[]>(session.ScopesJson) ?? [];
        return new CliLoginSessionResponse(session.Id, status, scopes, session.ExpiresAt, session.ApprovedAt, session.ExchangedAt);
    }
}
