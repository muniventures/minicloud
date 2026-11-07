using Minicloud.Api.Extensions;
using Minicloud.Common.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddMinicloudApi(builder.Configuration, builder.Environment);

var app = builder.Build();

await app.ConfigureMinicloudApiAsync();
app.MapHealthChecks("/health").AllowAnonymous();
app.MapControllers();
app.Run();

public partial class Program;
