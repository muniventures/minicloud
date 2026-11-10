namespace Minicloud.Api.Auth;

public sealed class AccountOriginValidator(IConfiguration configuration)
{
    private readonly HashSet<string> _allowedOrigins = configuration
        .GetSection("Cors:AllowedOrigins")
        .Get<string[]>()
        ?.Where(origin => !string.IsNullOrWhiteSpace(origin))
        .ToHashSet(StringComparer.Ordinal)
        ?? new HashSet<string>(StringComparer.Ordinal);

    public bool IsAllowed(string? origin) =>
        !string.IsNullOrEmpty(origin) && _allowedOrigins.Contains(origin);
}
