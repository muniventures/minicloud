namespace Minicloud.Api.Providers;

public sealed class ConsoleUrlProvider(IConfiguration configuration, IWebHostEnvironment environment) : IConsoleUrlProvider
{
    public string GetConsoleBaseUrl()
    {
        var configured = configuration["Console:BaseUrl"] ?? configuration["Dashboard:BaseUrl"];
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured.TrimEnd('/');
        }

        return environment.IsDevelopment() ? "http://localhost:3200" : "https://cloud.muni.dev";
    }
}
