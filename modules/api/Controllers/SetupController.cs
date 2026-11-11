using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using Minicloud.Api.Auth;
using Minicloud.Common.Contracts;
using Minicloud.Common.Services;

namespace Minicloud.Api.Controllers;

[ApiController]
[AllowAnonymous]
[Route("api/account/setup")]
public sealed class SetupController(
    BootstrapService bootstrap,
    AccountOriginValidator origins,
    IOptions<KratosOptions> options,
    IConfiguration configuration) : ControllerBase
{
    public const int BodyLimit = 64 * 1024;
    private static readonly string[] Fields =
        ["setupToken", "email", "password", "displayName", "organizationName", "organizationSlug"];

    [HttpGet]
    public async Task<IResult> Status(CancellationToken cancellationToken)
    {
        if (Request.QueryString.HasValue) return Invalid();
        var installationId = configuration["Bootstrap:InstallationId"];
        if (string.IsNullOrWhiteSpace(installationId))
            return Results.Ok(new BootstrapStatusResponse("blocked", null));
        try
        {
            return Results.Ok(await bootstrap.StatusAsync(installationId, cancellationToken));
        }
        catch (BootstrapException exception) when (exception.Code == "setup_state_conflict")
        {
            return Results.Ok(new BootstrapStatusResponse("blocked", null));
        }
        catch (BootstrapException exception)
        {
            return Failure(exception);
        }
    }

    [HttpPost]
    [EnableRateLimiting("operator-setup")]
    public async Task<IResult> Complete(CancellationToken cancellationToken)
    {
        var origin = Request.Headers.Origin;
        if (origin.Count != 1 || !origins.IsAllowed(origin.ToString()) ||
            !Uri.TryCreate(options.Value.DashboardOrigin, UriKind.Absolute, out var dashboard) ||
            dashboard.Scheme != Uri.UriSchemeHttps ||
            !string.Equals(origin.ToString(), options.Value.DashboardOrigin, StringComparison.Ordinal))
            return EndpointResults.Problem("csrf_invalid", "Request origin is invalid.", 403);
        if (Request.Headers.ContainsKey("Authorization"))
            return EndpointResults.Problem("unauthenticated", "Setup requires a one-time setup token.", 401);
        if (Request.QueryString.HasValue || !Request.HasJsonContentType())
            return Invalid();
        if (Request.ContentLength > BodyLimit)
            return TooLarge();

        CompleteSetupRequest request;
        try
        {
            // Bound chunked bodies too, before parsing or forwarding any secret.
            await using var buffer = new MemoryStream();
            var chunk = new byte[4096];
            int read;
            while ((read = await Request.Body.ReadAsync(chunk, cancellationToken)) > 0)
            {
                if (buffer.Length + read > BodyLimit) return TooLarge();
                buffer.Write(chunk, 0, read);
            }
            using var body = JsonDocument.Parse(buffer.ToArray(), new JsonDocumentOptions { MaxDepth = 8 });
            if (body.RootElement.ValueKind != JsonValueKind.Object) return Invalid();
            var properties = body.RootElement.EnumerateObject().ToArray();
            if (properties.Length != Fields.Length ||
                properties.Any(x => !Fields.Contains(x.Name, StringComparer.Ordinal) || x.Value.ValueKind != JsonValueKind.String) ||
                properties.Select(x => x.Name).Distinct(StringComparer.Ordinal).Count() != Fields.Length)
                return Invalid();
            var values = properties.ToDictionary(x => x.Name, x => x.Value.GetString()!, StringComparer.Ordinal);
            request = new(values["setupToken"], values["email"], values["password"], values["displayName"],
                values["organizationName"], values["organizationSlug"]);
        }
        catch (JsonException)
        {
            return Invalid();
        }
        catch (BadHttpRequestException)
        {
            return Invalid();
        }

        var installationId = configuration["Bootstrap:InstallationId"];
        if (string.IsNullOrWhiteSpace(installationId)) return Failure(BootstrapException.Conflict());
        try
        {
            return Results.Ok(await bootstrap.CompleteBrowserAsync(installationId, request, cancellationToken));
        }
        catch (BootstrapException exception)
        {
            return Failure(exception);
        }
    }

    private static IResult Invalid() =>
        EndpointResults.Problem("setup_request_invalid", "The setup form is invalid.", 400);
    private static IResult TooLarge() =>
        EndpointResults.Problem("setup_request_invalid", "The setup form is too large.", 413);

    private static IResult Failure(BootstrapException exception)
    {
        // Never forward dependency messages or submitted fields into public diagnostics.
        var message = exception.Code switch
        {
            "setup_token_invalid" => "The setup token is invalid.",
            "setup_token_expired" => "The setup token has expired.",
            "setup_in_progress" => "Setup is awaiting identity reconciliation.",
            "setup_already_completed" => "Setup has already completed. Sign in.",
            "setup_state_conflict" => "Check the operator fields or ask the host administrator to reconcile setup.",
            _ => "Identity service is temporarily unavailable."
        };
        return exception.Code is "setup_token_invalid" or "setup_token_expired" or "setup_in_progress" or
            "setup_already_completed" or "setup_state_conflict" or "auth_unavailable"
            ? EndpointResults.Problem(exception.Code, message, exception.StatusCode)
            : EndpointResults.Problem("auth_unavailable", message, 503);
    }
}
