using System.Net;
using System.Net.Http.Json;

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

    public static object Me(params string[] organizationIds) => new
    {
        id = "usr_1",
        email = "dev@example.com",
        organizations = organizationIds.Select(x => new { id = x, name = x, slug = x.Replace('_', '-'), role = "owner" }).ToArray()
    };

    public static HttpResponseMessage? TryMe(HttpRequestMessage request, params string[] organizationIds) =>
        request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath == "/api/me"
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(Me(organizationIds)) }
            : null;
}
