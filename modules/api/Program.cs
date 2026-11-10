using Minicloud.Api.Extensions;
using Minicloud.Common.Services;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddScoped<Minicloud.Api.Services.DatabaseBackupDownloadService>();
builder.Services.AddHttpClient(Minicloud.Api.Services.DatabaseBackupDownloadService.ClientName, client => client.Timeout = TimeSpan.FromSeconds(30))
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
builder.Services.AddScoped<DatabaseConnectionService>();
builder.Services.AddScoped<DatabaseBindingService>();
builder.Services.AddScoped<DatabaseNetworkService>();

builder.Services.AddMinicloudApi(builder.Configuration, builder.Environment);

builder.Services.AddScoped<DatabaseManagementService>();
builder.Services.AddScoped<DatabaseBackupPurgeService>();

var app = builder.Build();

await app.ConfigureMinicloudApiAsync();
app.MapHealthChecks("/health").AllowAnonymous();
app.MapControllers();
app.Run();

public partial class Program;
