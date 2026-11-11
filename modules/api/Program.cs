using Minicloud.Api.Extensions;
using Minicloud.Api.Services;
using Minicloud.Common.Services;

if (args.Length > 0 && args[0] == "bootstrap")
{
    Environment.ExitCode = await BootstrapCommand.RunAsync(args[1..]);
    return;
}

var builder = WebApplication.CreateBuilder(args);
if (args.Length > 0 && args[0] == "migrate")
{
    if (args.Length != 1)
    {
        throw new ArgumentException("The migrate command accepts no arguments.");
    }

    await DatabaseMigrationCommand.RunAsync(builder.Configuration, builder.Environment.EnvironmentName);
    return;
}

builder.Services.AddScoped<Minicloud.Api.Services.DatabaseBackupDownloadService>();
builder.Services.AddHttpClient(Minicloud.Api.Services.DatabaseBackupDownloadService.ClientName, client => client.Timeout = TimeSpan.FromSeconds(30))
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
builder.Services.AddScoped<DatabaseConnectionService>();
builder.Services.AddScoped<DatabaseBindingService>();
builder.Services.AddScoped<DatabaseNetworkService>();

builder.Services.AddMinicloudApi(builder.Configuration, builder.Environment);
builder.Services.AddMinicloudBootstrap();

builder.Services.AddScoped<DatabaseManagementService>();
builder.Services.AddScoped<DatabaseBackupPurgeService>();

var app = builder.Build();

await app.ConfigureMinicloudApiAsync();
app.MapHealthChecks("/health").AllowAnonymous();
app.MapControllers();
app.Run();

public partial class Program;
