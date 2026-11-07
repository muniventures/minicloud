using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Minicloud.Common.Registry;

namespace Minicloud.Api.Controllers;

[ApiController]
[AllowAnonymous]
[Route("internal/registry/maintenance")]
public sealed class RegistryMaintenanceController(IOptions<RegistryOptions> options, RegistryMaintenanceService maintenance) : ControllerBase
{
    public sealed record MaintenanceRequest(string Action, string? OwnerToken = null);

    [HttpPost]
    public async Task<IResult> Execute(MaintenanceRequest request, CancellationToken ct)
    {
        var header = Request.Headers["X-Minicloud-Registry-Operator"].ToString();
        if (!options.Value.Enabled || header.Length != 64 || !System.IO.File.Exists(options.Value.KeyPath)) { return Results.Unauthorized(); }
        var expected = RegistryTokens.Hash(await System.IO.File.ReadAllTextAsync(options.Value.KeyPath, ct));
        if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(header), Encoding.ASCII.GetBytes(expected))) { return Results.Unauthorized(); }
        Response.Headers.CacheControl = "no-store";
        if (request.Action == "begin") { return Results.Ok(new { ownerToken = await maintenance.BeginAsync(ct) }); }
        if (request.OwnerToken is null) { return Results.BadRequest(); }
        return request.Action switch
        {
            "sweep" => Results.Ok(new { retiredImages = await maintenance.SweepAsync(request.OwnerToken, ct) }),
            "finish" => Results.Ok(new { completed = await maintenance.FinishAsync(request.OwnerToken, ct) }),
            "unfence" => Results.Ok(new { completed = await maintenance.FinishAsync(request.OwnerToken, ct, collected: false) }),
            _ => Results.BadRequest()
        };
    }
}
