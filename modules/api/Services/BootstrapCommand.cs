using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Minicloud.Api.Auth;
using Minicloud.Common.Contracts;
using Minicloud.Common.Data;
using Minicloud.Common.Services;

namespace Minicloud.Api.Services;

public static class BootstrapCommand
{
    private const int InputLimit = 64 * 1024;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public static async Task<int> RunAsync(string[] args)
    {
        try
        {
            var parsed = Parse(args);
            var input = await ReadInputAsync(parsed.InputFile, Console.In, CancellationToken.None);
            var builder = WebApplication.CreateBuilder(Array.Empty<string>());
            builder.Logging.ClearProviders();
            var options = DatabaseMigrationCommand.ResolveOptions(builder.Configuration, builder.Environment.EnvironmentName);
            if (string.Equals(options.Provider, "Sqlite", StringComparison.OrdinalIgnoreCase))
                options.FallbackConnectionString = "Data Source=minicloud-dev.db";
            var connectionString = MinicloudDatabase.ResolveConnectionString(options);
            builder.Services.AddDbContext<MinicloudDbContext>(db =>
                MinicloudDatabase.ConfigureDbContext(db, options.Provider, connectionString));
            builder.Services.Configure<KratosOptions>(builder.Configuration.GetSection(KratosOptions.SectionName));
            builder.Services.AddSingleton(TimeProvider.System);
            builder.Services.AddSingleton<IIdGenerator, IdGenerator>();
            builder.Services.AddMinicloudBootstrap();
            await using var provider = builder.Services.BuildServiceProvider();
            await using var scope = provider.CreateAsyncScope();
            var service = scope.ServiceProvider.GetRequiredService<BootstrapService>();
            var tokenOutput = new ProtectedTokenOutput(parsed.OutputFile, Console.Out, !Console.IsOutputRedirected);
            var result = await ExecuteAsync(parsed.Operation, input, service, tokenOutput, CancellationToken.None);
            await Console.Out.WriteLineAsync(JsonSerializer.Serialize(result, Json));
            return 0;
        }
        catch (BootstrapException ex)
        {
            await Console.Error.WriteLineAsync(JsonSerializer.Serialize(new { code = ex.Code, message = ex.Message }, Json));
            return 1;
        }
        catch (Exception)
        {
            // Do not surface exception text: providers, file APIs and JSON parsers can
            // include protected input, credentials or upstream response bodies.
            await Console.Error.WriteLineAsync("""{"code":"bootstrap_failed","message":"Bootstrap failed; verify protected input and dependency availability."}""");
            return 1;
        }
    }

    public sealed class Input
    {
        public string InstallationId { get; set; } = "";
        public string OperationId { get; set; } = "";
        public string? SetupToken { get; set; }
        public string? Email { get; set; }
        public string? Password { get; set; }
        public string? DisplayName { get; set; }
        public string? OrganizationName { get; set; }
        public string? OrganizationSlug { get; set; }
        public string? UserId { get; set; }
        public string? OrganizationId { get; set; }
        public string? KratosIdentityId { get; set; }
    }

    public static async Task<object> ExecuteAsync(string operation, Input input, BootstrapService service,
        IBootstrapTokenOutput tokenOutput, CancellationToken ct = default)
    {
        switch (operation)
        {
            case "status":
                return await service.StatusAsync(input.InstallationId, ct);
            case "prepare":
                return await service.PrepareAsync(input.InstallationId, input.OperationId, tokenOutput, ct);
            case "rotate-token":
                return await service.RotateTokenAsync(input.InstallationId, input.OperationId, tokenOutput, ct);
            case "complete":
                return await service.CompleteHostAsync(input.InstallationId, input.OperationId,
                    new CompleteSetupRequest(input.SetupToken!, input.Email!, input.Password!, input.DisplayName!,
                        input.OrganizationName!, input.OrganizationSlug!), ct);
            case "bind-operator":
                return await service.BindOperatorAsync(input.InstallationId, input.OperationId,
                    input.UserId!, input.OrganizationId!, input.Password!, ct);
            case "recover-operator":
                return await service.RecoverOperatorAsync(input.InstallationId, input.OperationId,
                    input.UserId!, input.KratosIdentityId!, input.Password!, ct);
            default:
                throw BootstrapException.Conflict();
        }
    }

    private static (string Operation, string? InputFile, string? OutputFile) Parse(string[] args)
    {
        if (args.Length < 2 || args[0] is not ("status" or "prepare" or "complete" or "rotate-token" or "bind-operator" or "recover-operator"))
            throw BootstrapException.Conflict();
        string? input = null, output = null;
        var stdin = false;
        for (var index = 1; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--stdin" when !stdin && input is null:
                    stdin = true;
                    break;
                case "--input-file" when !stdin && input is null && index + 1 < args.Length:
                    input = args[++index];
                    break;
                case "--output-file" when output is null && index + 1 < args.Length &&
                    args[0] is "prepare" or "rotate-token":
                    output = args[++index];
                    break;
                default:
                    throw BootstrapException.Conflict();
            }
        }
        if (!stdin && input is null) throw BootstrapException.Conflict();
        if (args[0] is "prepare" or "rotate-token" && output is null && Console.IsOutputRedirected)
            throw new BootstrapException("setup_state_conflict", 409, "A protected token output file or interactive terminal is required.");
        return (args[0], input, output);
    }

    public static async Task<Input> ReadInputAsync(string? file, TextReader stdin, CancellationToken ct = default)
    {
        string json;
        if (file is null)
            json = await ReadBoundedAsync(stdin, ct);
        else
        {
            await using var stream = BootstrapProtectedInput.OpenRead(file);
            if (stream.Length > InputLimit)
                throw BootstrapException.Conflict();
            using var reader = new StreamReader(stream, Encoding.UTF8, true);
            json = await ReadBoundedAsync(reader, ct);
        }
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object ||
            document.RootElement.EnumerateObject().Select(x => x.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() !=
            document.RootElement.EnumerateObject().Count())
            throw BootstrapException.Conflict();
        return JsonSerializer.Deserialize<Input>(json, Json) ?? throw BootstrapException.Conflict();
    }

    private static async Task<string> ReadBoundedAsync(TextReader reader, CancellationToken ct)
    {
        var buffer = new char[4096];
        var text = new StringBuilder();
        int count;
        while ((count = await reader.ReadAsync(buffer, ct)) > 0)
        {
            if (text.Length + count > InputLimit) throw BootstrapException.Conflict();
            text.Append(buffer, 0, count);
        }
        var json = text.ToString();
        if (Encoding.UTF8.GetByteCount(json) > InputLimit) throw BootstrapException.Conflict();
        return json;
    }

    public sealed class ProtectedTokenOutput(string? path, TextWriter terminal, bool interactive) : IBootstrapTokenOutput
    {
        public async Task WriteAsync(string token, CancellationToken cancellationToken)
        {
            if (path is null)
            {
                if (!interactive) throw BootstrapException.Conflict();
                await terminal.WriteLineAsync(token.AsMemory(), cancellationToken);
                return;
            }
            if (OperatingSystem.IsWindows()) throw BootstrapException.Conflict();
            await using var stream = new FileStream(path, new FileStreamOptions
            {
                Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None,
                UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite
            });
            await using var writer = new StreamWriter(stream, new UTF8Encoding(false));
            await writer.WriteLineAsync(token.AsMemory(), cancellationToken);
            await writer.FlushAsync(cancellationToken);
            stream.Flush(true);
        }
    }
}
