using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Minicloud.Telemetry.Controllers;
using Minicloud.Telemetry.Data;
using Minicloud.Telemetry.Services;

namespace Minicloud.Telemetry;

public class Program
{
    public static async Task Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        // Options
        builder.Services.Configure<TelemetryOptions>(
            builder.Configuration.GetSection(TelemetryOptions.SectionName));

        var telemetryOptions = builder.Configuration.GetSection(TelemetryOptions.SectionName).Get<TelemetryOptions>()
            ?? new TelemetryOptions();

        // Database
        var connectionString = builder.Configuration.GetConnectionString("Telemetry")
            ?? builder.Configuration.GetConnectionString("DefaultConnection");

        builder.Services.AddDbContext<TelemetryDbContext>(options =>
        {
            if (!string.IsNullOrWhiteSpace(connectionString) &&
                (connectionString.StartsWith("Host=", StringComparison.OrdinalIgnoreCase) ||
                 connectionString.StartsWith("Server=", StringComparison.OrdinalIgnoreCase) ||
                 connectionString.StartsWith("postgres://", StringComparison.OrdinalIgnoreCase) ||
                 connectionString.StartsWith("postgresql://", StringComparison.OrdinalIgnoreCase)))
            {
                options.UseNpgsql(connectionString);
            }
            else
            {
                var dbPath = connectionString ?? "Data Source=telemetry.db";
                options.UseSqlite(dbPath);
            }
        });

        // Singletons & Services
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddHttpClient();

        builder.Services.AddSingleton<TelemetryStatusReporter>();
        builder.Services.AddSingleton<TelemetryQueryLimiter>();
        builder.Services.AddSingleton<OtlpIngestionLimiter>();

        builder.Services.AddScoped<TelemetryAuthorityService>();
        builder.Services.AddScoped<TelemetryCredentialAuthService>();
        builder.Services.AddScoped<TelemetryQuotaService>();
        builder.Services.AddScoped<OtlpNormalizationService>();
        builder.Services.AddScoped<OtlpLokiForwarder>();
        builder.Services.AddScoped<TelemetryQueryService>();
        builder.Services.AddScoped<GrafanaSessionService>();
        builder.Services.AddScoped<GrafanaQueryGateway>();
        builder.Services.AddScoped<TelemetryCleanupService>();

        builder.Services.AddCors(cors =>
        {
            cors.AddDefaultPolicy(policy =>
            {
                if (!string.IsNullOrWhiteSpace(telemetryOptions.AccessOrigin))
                {
                    policy.WithOrigins(telemetryOptions.AccessOrigin.TrimEnd('/'))
                          .AllowCredentials()
                          .AllowAnyHeader()
                          .AllowAnyMethod();
                }
                else
                {
                    policy.SetIsOriginAllowed(_ => true)
                          .AllowCredentials()
                          .AllowAnyHeader()
                          .AllowAnyMethod();
                }
            });
        });

        builder.Services.AddControllers();

        var app = builder.Build();

        // Ensure DB is initialized
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TelemetryDbContext>();
            await db.Database.EnsureCreatedAsync();
        }

        app.UseRouting();
        app.UseCors();

        app.MapControllers();

        await app.RunAsync();
    }
}
