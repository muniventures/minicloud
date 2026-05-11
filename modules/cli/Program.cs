using Minicloud.Cli;
using Minicloud.Cli.Api;
using Minicloud.Cli.Auth;
using Minicloud.Cli.Commands;

var console = new SystemConsole();
var environment = CliEnvironment.FromEnvironment();
var tokenStore = new TokenStore(environment);
var apiClient = new MinicloudApiClient(environment, tokenStore);
var app = new CliApplication(console, environment, tokenStore, apiClient);

return await app.RunAsync(args, CancellationToken.None);
