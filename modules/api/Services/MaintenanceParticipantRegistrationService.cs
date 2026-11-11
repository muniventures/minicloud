using Minicloud.Common.Services;

namespace Minicloud.Api.Services;

public sealed class MaintenanceParticipantRegistrationService(MaintenanceParticipant participant) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) => participant.RunRegistrationLoopAsync(stoppingToken);
}
