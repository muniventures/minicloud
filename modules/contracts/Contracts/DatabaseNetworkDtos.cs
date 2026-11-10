namespace Minicloud.Common.Contracts;

public sealed record CreateDatabaseNetworkRequest(string OrganizationId, string Name, string Plan, string IpRange = "10.72.0.0/16", string SubnetRange = "10.72.1.0/24");
public sealed record DatabasePrivateAttachment(string NetworkId, string ProviderNetworkId, string ServerInstanceId, string IpAddress, string NetworkZone, string SubnetRange);
