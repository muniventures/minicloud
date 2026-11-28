namespace Selfrun.RuntimeAgent;

public sealed class RuntimeStatusPublisher(TimeProvider? timeProvider = null)
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(1);
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;
    private RuntimeStatusIngestRequest? _published;
    private long _publishedAt;

    public async Task PublishAsync(RuntimeStatusIngestRequest status,
        Func<RuntimeStatusIngestRequest, CancellationToken, Task> send, CancellationToken cancellationToken)
    {
        if (_published is not null && _clock.GetElapsedTime(_publishedAt) < RefreshInterval &&
            _published.AgentVersion == status.AgentVersion && _published.ComposeProject == status.ComposeProject &&
            _published.Caddy == status.Caddy && _published.Services.SequenceEqual(status.Services) &&
            (_published.HttpRequests ?? []).SequenceEqual(status.HttpRequests ?? []))
        {
            return;
        }

        var publishedAt = _clock.GetTimestamp();
        await send(status, cancellationToken);
        _published = status;
        _publishedAt = publishedAt;
    }
}
