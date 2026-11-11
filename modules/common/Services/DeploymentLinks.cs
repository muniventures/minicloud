namespace Minicloud.Common.Services;

/// <summary>Customer links address a deployment by number and always carry its organization scope.</summary>
public static class DeploymentLinks
{
    public static string ApiPath(string organizationId, string number) =>
        $"/api/deployments/{Uri.EscapeDataString(number)}?organizationId={Uri.EscapeDataString(organizationId)}";

    public static string ConsoleUrl(string consoleBaseUrl, string organizationId, string number) =>
        $"{consoleBaseUrl.TrimEnd('/')}/deployments/{Uri.EscapeDataString(number)}?organizationId={Uri.EscapeDataString(organizationId)}";
}
