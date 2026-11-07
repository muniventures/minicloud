namespace Minicloud.Common.Providers.Dns;

public interface ICloudflareDnsClient
{
    Task DeleteDnsRecordsAsync(string hostname, CancellationToken cancellationToken = default);
    Task UpsertDnsRecordAsync(string hostname, string ipAddress, CancellationToken cancellationToken = default);
    Task WaitForPropagationAsync(string hostname, string expectedIp, TimeSpan timeout, CancellationToken cancellationToken = default);
}
