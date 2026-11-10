namespace Minicloud.Api.Auth;

public sealed class KratosOptions
{
    public const string SectionName = "Kratos";

    public string PublicBaseUrl { get; set; } = "";
    public string AdminBaseUrl { get; set; } = "";
    public string DashboardOrigin { get; set; } = "";
    public bool GoogleEnabled { get; set; }
    public bool EmailRecoveryEnabled { get; set; }
}

public sealed class KratosUnavailableException() : Exception("The identity service is temporarily unavailable.");

public interface IKratosSessionValidator
{
    Task<bool> IsSessionActiveAsync(string sessionId, string identityId, CancellationToken cancellationToken);
    Task RevokeSessionAsync(string sessionId, CancellationToken cancellationToken);
}
