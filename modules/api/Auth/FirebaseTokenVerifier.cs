using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace Minicloud.Api.Auth;

public sealed record VerifiedFirebaseToken(string Uid, string Email, string? DisplayName);

public interface IFirebaseTokenVerifier
{
    Task<VerifiedFirebaseToken?> VerifyAsync(string token, CancellationToken cancellationToken);
}

public sealed class FirebaseTokenVerifier : IFirebaseTokenVerifier
{
    private readonly FirebaseAuthenticationOptions _options;
    private readonly IConfigurationManager<OpenIdConnectConfiguration> _configurationManager;
    private readonly ILogger<FirebaseTokenVerifier> _logger;
    private readonly JsonWebTokenHandler _tokenHandler = new();

    public FirebaseTokenVerifier(IOptions<FirebaseAuthenticationOptions> options, ILogger<FirebaseTokenVerifier> logger)
        : this(
            options,
            logger,
            CreateConfigurationManager(options.Value.ProjectId))
    {
    }

    public FirebaseTokenVerifier(
        IOptions<FirebaseAuthenticationOptions> options,
        ILogger<FirebaseTokenVerifier> logger,
        IConfigurationManager<OpenIdConnectConfiguration> configurationManager)
    {
        _options = options.Value;
        if (string.IsNullOrWhiteSpace(_options.ProjectId))
        {
            throw new InvalidOperationException("Firebase:ProjectId must be configured for dashboard authentication.");
        }

        _logger = logger;
        _configurationManager = configurationManager;
    }

    public async Task<VerifiedFirebaseToken?> VerifyAsync(string token, CancellationToken cancellationToken)
    {
        try
        {
            var configuration = await _configurationManager.GetConfigurationAsync(cancellationToken);
            var issuer = GetIssuer(_options.ProjectId);

            var result = await _tokenHandler.ValidateTokenAsync(token, new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidIssuer = issuer,
                ValidateAudience = true,
                ValidAudience = _options.ProjectId,
                ValidateIssuerSigningKey = true,
                IssuerSigningKeys = configuration.SigningKeys,
                ValidateLifetime = true,
                RequireExpirationTime = true,
                RequireSignedTokens = true,
                ClockSkew = TimeSpan.FromMinutes(2)
            });

            if (!result.IsValid || result.ClaimsIdentity is null)
            {
                return null;
            }

            var uid = result.ClaimsIdentity.FindFirst("user_id")?.Value
                ?? result.ClaimsIdentity.FindFirst(JwtRegisteredClaimNames.Sub)?.Value;
            if (string.IsNullOrWhiteSpace(uid) ||
                (_options.AllowedUserIds.Length > 0 && !_options.AllowedUserIds.Contains(uid, StringComparer.Ordinal)))
            {
                return null;
            }

            var email = result.ClaimsIdentity.FindFirst(JwtRegisteredClaimNames.Email)?.Value;
            if (string.IsNullOrWhiteSpace(email))
            {
                return null;
            }

            var displayName = result.ClaimsIdentity.FindFirst("name")?.Value;
            return new VerifiedFirebaseToken(uid, email!, displayName);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning(exception, "Firebase ID-token validation failed.");
            return null;
        }
    }

    private static ConfigurationManager<OpenIdConnectConfiguration> CreateConfigurationManager(string projectId)
    {
        var issuer = GetIssuer(projectId);
        return new ConfigurationManager<OpenIdConnectConfiguration>(
            $"{issuer}/.well-known/openid-configuration",
            new OpenIdConnectConfigurationRetriever());
    }

    private static string GetIssuer(string projectId) => $"https://securetoken.google.com/{projectId}";
}
