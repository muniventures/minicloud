namespace Minicloud.Api.Auth;

public sealed class FirebaseAuthenticationOptions
{
    public const string SectionName = "Firebase";

    public string ProjectId { get; set; } = "";
    public string[] AllowedUserIds { get; set; } = [];
}
