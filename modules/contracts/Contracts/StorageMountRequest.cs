// SPDX-License-Identifier: MPL-2.0
namespace Minicloud.Common.Contracts;

public sealed record StorageMountRequest(int SizeGb, string MountPath, int Uid = 0, int Gid = 0);

public sealed record AppStorageVolumeResponse(string Name, string Provider, string? ProviderVolumeId,
    string Region, int SizeGb, int Uid, int Gid, string Status);
