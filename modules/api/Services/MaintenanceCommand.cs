using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Minicloud.Common.Data;
using Minicloud.Common.Services;

namespace Minicloud.Api.Services;

/// <summary>
/// Protected host command: <c>maintenance &lt;status|fence|drain-status|release|transfer&gt; (--stdin | --input-file PATH)</c>.
/// Writes one JSON document to stdout on success or a refusal, and a JSON error to stderr otherwise.
/// </summary>
public static class MaintenanceCommand
{
    public const int ExitSuccess = 0;
    public const int ExitError = 1;
    public const int ExitNotDrained = 2;
    public const int ExitRefused = 3;

    private const int InputLimit = 16 * 1024;
    private static readonly string[] Operations = ["status", "fence", "drain-status", "release", "transfer"];
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };

    public sealed class Input
    {
        public string InstallationId { get; set; } = "";
        public string? OperationId { get; set; }
        public string? HostId { get; set; }
    }

    public static async Task<int> RunAsync(string[] args)
    {
        try
        {
            var (operation, inputFile) = Parse(args);
            var input = await ReadInputAsync(inputFile, Console.In, CancellationToken.None);
            var builder = WebApplication.CreateBuilder(Array.Empty<string>());
            builder.Logging.ClearProviders();
            var options = DatabaseMigrationCommand.ResolveOptions(builder.Configuration, builder.Environment.EnvironmentName);
            if (string.Equals(options.Provider, "Sqlite", StringComparison.OrdinalIgnoreCase))
                options.FallbackConnectionString = "Data Source=minicloud-dev.db";
            var connectionString = MinicloudDatabase.ResolveConnectionString(options);
            var hostId = MaintenanceAuthorityOptions.ResolveHostId(builder.Configuration);
            builder.Services.AddDbContext<MinicloudDbContext>(db =>
                MinicloudDatabase.ConfigureDbContext(db, options.Provider, connectionString));
            await using var provider = builder.Services.BuildServiceProvider();
            await using var scope = provider.CreateAsyncScope();
            var service = new MaintenanceAuthorityService(
                scope.ServiceProvider.GetRequiredService<MinicloudDbContext>(), TimeProvider.System, hostId);
            return await ExecuteAsync(operation, input, service, Console.Out, CancellationToken.None);
        }
        catch (MaintenanceException ex) when (!ex.Refused)
        {
            await WriteErrorAsync(Console.Error, ex.Code, ex.Message);
            return ExitError;
        }
        catch (BootstrapException)
        {
            await WriteErrorAsync(Console.Error, "invalid_input", "Maintenance input must be an owner-only regular file or stdin JSON object.");
            return ExitError;
        }
        catch (Exception)
        {
            // Never surface exception text: providers and parsers may echo connection strings or input.
            await WriteErrorAsync(Console.Error, "maintenance_failed", "Maintenance command failed; verify input and database availability.");
            return ExitError;
        }
    }

    /// <summary>Runs one operation and writes its JSON result. Returns the process exit code.</summary>
    public static async Task<int> ExecuteAsync(string operation, Input input, MaintenanceAuthorityService service, TextWriter output, CancellationToken ct = default)
    {
        try
        {
            switch (operation)
            {
                case "status":
                    await WriteAsync(output, new { result = "ok", operation, authority = await service.StatusAsync(input.InstallationId, ct) });
                    return ExitSuccess;
                case "fence":
                    await WriteAsync(output, new { result = "ok", operation, authority = await service.FenceAsync(input.InstallationId, RequireOperation(input), ct) });
                    return ExitSuccess;
                case "release":
                    await WriteAsync(output, new { result = "ok", operation, authority = await service.ReleaseAsync(input.InstallationId, RequireOperation(input), ct) });
                    return ExitSuccess;
                case "transfer":
                    await WriteAsync(output, new { result = "ok", operation, authority = await service.TransferAsync(input.InstallationId, RequireOperation(input), input.HostId, ct) });
                    return ExitSuccess;
                case "drain-status":
                    var status = await service.DrainStatusAsync(input.InstallationId, ct);
                    // Drained only counts as success when this host holds a fence; otherwise new work may still arrive.
                    var ready = status.Drained && status.Authority.State == Minicloud.Common.Models.MaintenanceAuthorityStates.Fenced &&
                        status.Authority.LocalHostAuthorized &&
                        (input.OperationId is null || input.OperationId == status.Authority.HolderOperationId);
                    await WriteAsync(output, new { result = ready ? "drained" : "not_drained", operation, status });
                    return ready ? ExitSuccess : ExitNotDrained;
                default:
                    throw new MaintenanceException("invalid_input", "Unknown maintenance operation.", refused: false);
            }
        }
        catch (MaintenanceException ex) when (ex.Refused)
        {
            await WriteAsync(output, new { result = "refused", operation, code = ex.Code, message = ex.Message });
            return ExitRefused;
        }
    }

    private static string RequireOperation(Input input) => input.OperationId is { Length: > 0 } id
        ? id
        : throw new MaintenanceException("invalid_input", "operationId is required.", refused: false);

    public static (string Operation, string? InputFile) Parse(string[] args)
    {
        if (args.Length < 2 || !Operations.Contains(args[0]))
            throw new MaintenanceException("invalid_input", "Usage: maintenance <status|fence|drain-status|release|transfer> (--stdin | --input-file PATH).", refused: false);
        string? input = null;
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
                default:
                    throw new MaintenanceException("invalid_input", "Unsupported maintenance arguments.", refused: false);
            }
        }
        if (!stdin && input is null)
            throw new MaintenanceException("invalid_input", "Either --stdin or --input-file is required.", refused: false);
        return (args[0], input);
    }

    public static async Task<Input> ReadInputAsync(string? file, TextReader stdin, CancellationToken ct = default)
    {
        string json;
        if (file is null)
            json = await ReadBoundedAsync(stdin, ct);
        else
        {
            await using var stream = BootstrapProtectedInput.OpenRead(file);
            if (stream.Length > InputLimit) throw InvalidInput();
            using var reader = new StreamReader(stream, Encoding.UTF8, true);
            json = await ReadBoundedAsync(reader, ct);
        }
        try
        {
            using var document = JsonDocument.Parse(json);
            var names = document.RootElement.ValueKind == JsonValueKind.Object
                ? document.RootElement.EnumerateObject().Select(x => x.Name).ToList()
                : null;
            if (names is null || names.Distinct(StringComparer.OrdinalIgnoreCase).Count() != names.Count)
                throw InvalidInput();
            return JsonSerializer.Deserialize<Input>(json, Json) ?? throw InvalidInput();
        }
        catch (JsonException)
        {
            throw InvalidInput();
        }
    }

    private static async Task<string> ReadBoundedAsync(TextReader reader, CancellationToken ct)
    {
        var buffer = new char[4096];
        var text = new StringBuilder();
        int count;
        while ((count = await reader.ReadAsync(buffer, ct)) > 0)
        {
            if (text.Length + count > InputLimit) throw InvalidInput();
            text.Append(buffer, 0, count);
        }
        return text.ToString();
    }

    private static MaintenanceException InvalidInput() =>
        new("invalid_input", "Maintenance input must be a JSON object with installationId and optional operationId/hostId.", refused: false);

    private static Task WriteAsync(TextWriter output, object value) =>
        output.WriteLineAsync(JsonSerializer.Serialize(value, Json));

    private static Task WriteErrorAsync(TextWriter output, string code, string message) =>
        output.WriteLineAsync(JsonSerializer.Serialize(new { code, message }, Json));
}
