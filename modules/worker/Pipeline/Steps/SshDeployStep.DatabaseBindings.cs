using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Minicloud.Common.Data;
using Minicloud.Common.Services;
using Minicloud.Worker.Pipeline;
using Npgsql;

namespace Minicloud.Worker.Pipeline.Steps;

public sealed partial class SshDeployStep
{
    internal static async Task<IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>> ResolveDatabaseBindingsAsync(
        MinicloudDbContext db, SecretEncryptionService? encryption, string organizationId, string appId, IReadOnlySet<string> serviceNames, CancellationToken ct)
    {
        var bindings = await db.ManagedDatabaseBindings.AsNoTracking().Where(x => x.OrganizationId == organizationId && x.AppId == appId && x.Status != "deleted").ToListAsync(ct);
        var result = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal);
        foreach (var binding in bindings.Where(x => serviceNames.Contains(x.ServiceName)))
        {
            if (binding.Status == "revoked")
            {
                result.Add(binding.ServiceName, new Dictionary<string, string> { ["DATABASE_URL"] = "", ["ConnectionStrings__Minicloud"] = "" });
                continue;
            }
            if (binding.Status != "applied" || binding.Host != "host.docker.internal" || binding.Port is < 20000 or > 60000)
                throw new PipelineStepException("database_binding_unavailable", "The selected service database binding is not ready.");
            var database = await db.ManagedDatabases.AsNoTracking().SingleOrDefaultAsync(x => x.Id == binding.DatabaseId && x.OrganizationId == organizationId && x.Engine == "postgres" && x.Status == "ready", ct);
            if (database?.EncryptedCredentialsJson is null) throw new PipelineStepException("database_binding_unavailable", "Bound database credentials are unavailable.");
            if (encryption is null) throw new PipelineStepException("database_binding_unavailable", "Database credential custody is unavailable.");
            var credentials = JsonSerializer.Deserialize<DatabaseConnectionCredentials>(encryption.Decrypt(database.EncryptedCredentialsJson), JsonOptions)
                ?? throw new PipelineStepException("database_binding_unavailable", "Bound database credentials are unavailable.");
            var connection = new NpgsqlConnectionStringBuilder { Host = binding.Host, Port = binding.Port, Database = credentials.DatabaseName, Username = credentials.Username, Password = credentials.Password, SslMode = SslMode.Disable };
            var url = $"postgresql://{Uri.EscapeDataString(credentials.Username)}:{Uri.EscapeDataString(credentials.Password)}@{binding.Host}:{binding.Port}/{Uri.EscapeDataString(credentials.DatabaseName)}?sslmode=disable";
            result.Add(binding.ServiceName, new Dictionary<string, string>(StringComparer.Ordinal) { ["DATABASE_URL"] = url, ["ConnectionStrings__Minicloud"] = connection.ConnectionString });
        }
        return result;
    }
}
