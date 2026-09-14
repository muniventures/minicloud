using Minicloud.Cli;
using Minicloud.Cli.Api;
using Minicloud.Cli.Auth;
using Minicloud.Cli.Commands;

using var cts = new CancellationTokenSource();
try
{
    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true;
        cts.Cancel();
    };
}
catch (InvalidOperationException)
{
    // Ignore when running without a console
}

var console = new SystemConsole();
var environment = CliEnvironment.FromEnvironment();
var tokenStore = new TokenStore(environment);
var apiClient = new MinicloudApiClient(environment, tokenStore);
var app = new CliApplication(console, environment, tokenStore, apiClient);

return await app.RunAsync(args, cts.Token);
