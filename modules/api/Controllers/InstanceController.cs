// SPDX-License-Identifier: AGPL-3.0-only
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Minicloud.Common.Contracts;
using Minicloud.Common.Services;

namespace Minicloud.Api.Controllers;

[ApiController]
[Route("api/instance")]
public sealed class InstanceController : ControllerBase
{
    [HttpGet]
    [AllowAnonymous]
    public InstanceInfoResponse Get([FromServices] IOptions<ManagedDomainOptions> options) =>
        new("minicloud", 1, options.Value.Suffix);
}
