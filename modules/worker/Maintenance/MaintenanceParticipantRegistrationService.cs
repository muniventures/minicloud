using Microsoft.Extensions.Hosting;
using Minicloud.Common.Services;

namespace Minicloud.Worker.Maintenance;

public sealed class MaintenanceParticipantRegistrationService(MaintenanceParticipant participant) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) => participant.RunRegistrationLoopAsync(stoppingToken);
}
