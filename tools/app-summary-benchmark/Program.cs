using System.Data.Common;
using System.Diagnostics;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Minicloud.Api.Controllers;
using Minicloud.Common.Contracts;
using Minicloud.Common.Data;
using Minicloud.Common.Models;
using Minicloud.Common.Services;

// This executable refuses to create/clear databases: start a dedicated empty local
// PostgreSQL database first. Migrations and synthetic inserts affect only that database.
var connection = Environment.GetEnvironmentVariable("MINICLOUD_PERF_DATABASE")
    ?? throw new InvalidOperationException("Set MINICLOUD_PERF_DATABASE to a disposable local PostgreSQL database.");
var cs = new Npgsql.NpgsqlConnectionStringBuilder(connection);
if (cs.Host is not ("localhost" or "127.0.0.1") || string.IsNullOrEmpty(cs.Database) || !cs.Database.StartsWith("apps_perf", StringComparison.Ordinal))
    throw new InvalidOperationException("Benchmark requires a local database named apps_perf*.");
var recorder = new Commands();
DbContextOptions<MinicloudDbContext> Options(bool record = false) => new DbContextOptionsBuilder<MinicloudDbContext>()
    .UseNpgsql(connection, o => o.CommandTimeout(10)).AddInterceptors(record ? [recorder] : []).Options;
await using (var db = new MinicloudDbContext(Options()))
{
    await db.Database.OpenConnectionAsync();
    await using var check = db.Database.GetDbConnection().CreateCommand();
    check.CommandText = "SELECT EXISTS (SELECT 1 FROM information_schema.tables WHERE table_schema = 'public')";
    if ((bool)(await check.ExecuteScalarAsync())!) throw new InvalidOperationException("Use a fresh empty benchmark database.");
    await db.Database.MigrateAsync();
}
Console.WriteLine($".NET {Environment.Version}; CPUs {Environment.ProcessorCount}; {System.Runtime.InteropServices.RuntimeInformation.OSDescription}");
await using (var db = new MinicloudDbContext(Options()))
{
    await db.Database.OpenConnectionAsync();
    using var cmd = db.Database.GetDbConnection().CreateCommand(); cmd.CommandText = "SELECT version()";
    Console.WriteLine(await cmd.ExecuteScalarAsync());
}
foreach (var (name, roots, history) in new[] { ("small", 6, 10), ("history", 6, 1000), ("scaling", 50, 1000) })
{
    await Seed(name, roots, history);
    await Measure(name, "apps", () => Fetch(name));
}
await using (var db = new MinicloudDbContext(Options(true)))
{
    recorder.Sql.Clear();
    await new AppSummaryReader(db).ReadEnvironmentsAsync("history", db.Apps, default);
    var dir = Environment.GetEnvironmentVariable("MINICLOUD_PERF_OUTPUT") ?? Path.GetFullPath(".local/artifacts/apps-data-fetch");
    Directory.CreateDirectory(dir);
    for (var i = 0; i < recorder.Sql.Count; i++)
    {
        var entry = recorder.Sql[i];
        await File.WriteAllTextAsync(Path.Combine(dir, $"query-{i + 1}.sql"), entry.Sql);
        await using var cmd = db.Database.GetDbConnection().CreateCommand();
        await db.Database.OpenConnectionAsync();
        cmd.CommandText = "EXPLAIN (ANALYZE, BUFFERS) " + entry.Sql;
        foreach (var p in entry.Parameters) cmd.Parameters.Add(new Npgsql.NpgsqlParameter(p.Name, p.Value));
        await using var rows = await cmd.ExecuteReaderAsync();
        var lines = new List<string>(); while (await rows.ReadAsync()) lines.Add(rows.GetString(0));
        await File.WriteAllLinesAsync(Path.Combine(dir, $"plan-{i + 1}.txt"), lines);
    }
    Console.WriteLine($"SQL and PostgreSQL plans: {dir}");
}

async Task<string> Fetch(string org)
{
    await using var db = new MinicloudDbContext(Options(true));
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    var context = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity([
        new Claim("auth_kind", "api_key"), new Claim("organization_id", org), new Claim("scope", ApiKeyScopes.AppsRead)], "fixture")) };
    var controller = new ApplicationsController { ControllerContext = new ControllerContext { HttpContext = context } };
    var result = await controller.ListApplications(org, db, new ControlPlaneAuthorization(db), TimeProvider.System, timeout.Token);
    var applications = (IReadOnlyList<ApplicationResponse>)((IValueHttpResult)result).Value!;
    var expected = org == "small" ? 10 : 1000;
    foreach (var env in applications.SelectMany(x => x.Environments))
        if (env.Domains.Count != 8 || env.LatestDeployment?.Id != $"{env.Id}-deploy-{expected:D5}")
            throw new InvalidOperationException($"Summary contract failed for synthetic record {env.Id}.");
    return JsonSerializer.Serialize(applications);
}
async Task Measure(string org, string label, Func<Task<string>> fetch)
{
    var cold = Stopwatch.StartNew(); var json = await fetch(); cold.Stop();
    for (int i = 0; i < 10; i++) await fetch();
    recorder.Sql.Clear();
    var allocated = GC.GetTotalAllocatedBytes(true); var times = new List<double>();
    for (int i = 0; i < 50; i++) { var sw = Stopwatch.StartNew(); await fetch(); times.Add(sw.Elapsed.TotalMilliseconds); }
    allocated = GC.GetTotalAllocatedBytes(true) - allocated;
    times.Sort(); Console.WriteLine($"{org} {label}: cold={cold.Elapsed.TotalMilliseconds:F1}ms median={times[25]:F1}ms p95={times[47]:F1}ms bytes={System.Text.Encoding.UTF8.GetByteCount(json)} allocated/request={allocated / 50} commands/request={recorder.Sql.Count / 50}");
    var concurrent = Enumerable.Range(0, 5).Select(async _ => { var samples = new List<double>(); for (int i = 0; i < 10; i++) { var sw = Stopwatch.StartNew(); await fetch(); samples.Add(sw.Elapsed.TotalMilliseconds); } return samples; });
    var results = (await Task.WhenAll(concurrent)).SelectMany(x => x).Order().ToArray();
    Console.WriteLine($"{org} {label}: five-client p95={results[47]:F1}ms errors=0");
}
async Task Seed(string org, int roots, int history)
{
    await using var db = new MinicloudDbContext(Options());
    var now = DateTimeOffset.Parse("2026-11-01T00:00:00Z");
    db.Organizations.Add(new Organization { Id = org, Name = org, Slug = org, CreatedAt = now, UpdatedAt = now });
    for (var r = 0; r < roots; r++)
    {
        var root = $"{org}-{r:D3}-0"; var application = $"appl-{org}-{r:D3}";
        db.Applications.Add(new MinicloudApplication { Id = application, OrganizationId = org, Name = root, Slug = root, NormalizedSlug = root, DefaultEnvironmentId = root, LegacyRootAppId = root, CreatedAt = now, UpdatedAt = now });
        for (var e = 0; e < 3; e++)
        {
            var id = $"{org}-{r:D3}-{e}";
            db.Apps.Add(new MinicloudApp { Id = id, OrganizationId = org, ApplicationId = application, Name = id, Slug = id,
                ParentAppId = e == 0 ? null : root, EnvironmentName = $"env{e}", NormalizedEnvironmentName = $"env{e}",
                BranchName = e == 2 ? "feature" : null, EnvironmentKind = e == 2 ? EnvironmentKinds.Preview : EnvironmentKinds.Persistent,
                Plan = AppPlan.P0H0, CreatedAt = now, UpdatedAt = now });
            for (var d = 0; d < 9; d++) db.Domains.Add(new Domain { Id = $"domain-{id}-{d}", OrganizationId = org, AppId = id,
                ServiceName = "web", Kind = "custom", DnsStatus = "ready", ApplyStatus = "applied", Hostname = $"d{d}-{id}.example.test", Status = d == 8 ? "disabled" : "active", SslStatus = "active", CreatedAt = now, UpdatedAt = now });
        }
    }
    await db.SaveChangesAsync();
    // Generate historical rows on the server; synthetic data only. Newest rows use varied statuses.
    await db.Database.ExecuteSqlInterpolatedAsync($"""
        INSERT INTO deployments(id,organization_id,app_id,database,status,created_at,updated_at)
        SELECT a.id || '-deploy-' || lpad(n::text, 5, '0'), a.organization_id, a.id, 'None',
          CASE WHEN n % 3 = 0 THEN 'Failed' ELSE 'Succeeded' END,
          TIMESTAMPTZ '2026-11-01 00:00:00+00' + n * INTERVAL '1 second', TIMESTAMPTZ '2026-11-01 00:00:00+00'
        FROM apps a CROSS JOIN generate_series(1, {history}) n WHERE a.organization_id = {org}
        """);
    await db.Database.ExecuteSqlRawAsync("ANALYZE");
}
sealed class Commands : DbCommandInterceptor
{
    public List<(string Sql, (string Name, object Value)[] Parameters)> Sql { get; } = [];
    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
    {
        lock (Sql) Sql.Add((command.CommandText, command.Parameters.Cast<DbParameter>().Select(p => (p.ParameterName, p.Value!)).ToArray()));
        return ValueTask.FromResult(result);
    }
}
