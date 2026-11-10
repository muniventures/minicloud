namespace Minicloud.Worker.Databases;

public sealed class DatabasePlatformOptions
{
    public const string SectionName = "Worker:Databases";
    public string RepositoryImage { get; set; } = "";
}
