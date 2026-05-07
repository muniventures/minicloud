using Municloud.Cli;
using Municloud.Cli.Api;
using Municloud.Cli.Auth;
using Municloud.Cli.Commands;

var console = new SystemConsole();
var environment = CliEnvironment.FromEnvironment();
var tokenStore = new TokenStore(environment);
var apiClient = new MunicloudApiClient(environment, tokenStore);
var app = new CliApplication(console, environment, tokenStore, apiClient);

return await app.RunAsync(args, CancellationToken.None);
