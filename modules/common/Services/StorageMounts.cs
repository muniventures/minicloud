using System.Text.Json;
using Minicloud.Common.Contracts;

namespace Minicloud.Common.Services;

public static class StorageMounts
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
    public static IReadOnlyDictionary<string, StorageMountRequest> Read(string? json) =>
        json is null ? new Dictionary<string, StorageMountRequest>() : JsonSerializer.Deserialize<Dictionary<string, StorageMountRequest>>(json, Options)!;
}
