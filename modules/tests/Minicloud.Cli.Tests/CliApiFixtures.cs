namespace Minicloud.Tests;

internal static class CliApiFixtures
{
    public static object Application(string id, string organizationId, string environmentId, string name = "Demo", string slug = "demo", string database = "sqlite") => new
    {
        id, organizationId, name, slug, defaultEnvironmentId = environmentId,
        environments = new[] { EnvironmentSummary(environmentId, "production", database) }
    };

    public static object EnvironmentSummary(string id, string name, string database = "sqlite", string? deletionStatus = null) => new
    {
        id, name, kind = "persistent", plan = "p0", database, deletionStatus
    };
}
