// SPDX-License-Identifier: AGPL-3.0-only
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Numerics;
using Minicloud.Common.Contracts;

namespace Minicloud.Api.Services;

public sealed class ServerReleaseOptions
{
    public string? ManifestPath { get; set; }
    public ServerReleaseMetadata? Metadata { get; set; }
}

public sealed record ServerReleaseMetadata(
    int SchemaVersion,
    string ServerVersion,
    string ReleaseTag,
    string SourceRevision,
    CliCompatibilityInfo CliCompatibility)
{
    private static readonly Regex VersionPattern = new(
        @"\A(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(-[0-9A-Za-z-]+(\.[0-9A-Za-z-]+)*)?\z",
        RegexOptions.CultureInvariant);
    private static readonly Regex RevisionPattern = new(@"\A[0-9a-f]{40}\z", RegexOptions.CultureInvariant);

    public static ServerReleaseMetadata? Load(string? path)
    {
        if (path is null)
            return null;
        if (string.IsNullOrWhiteSpace(path))
            throw new InvalidOperationException("ServerRelease:ManifestPath must name the verified release metadata file.");

        var metadata = JsonSerializer.Deserialize<ServerReleaseMetadata>(
            File.ReadAllText(path), new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? throw new InvalidOperationException("Server release metadata is empty.");
        var compatibility = metadata.CliCompatibility;
        if (metadata.SchemaVersion != 1
            || !IsVersion(metadata.ServerVersion)
            || metadata.ReleaseTag != $"server-v{metadata.ServerVersion}"
            || metadata.SourceRevision is null || !RevisionPattern.IsMatch(metadata.SourceRevision)
            || compatibility is null || compatibility.ProtocolVersion != 1
            || compatibility.SourceRevisions is null
            || compatibility.SourceRevisions.Any(revision => revision is null || !RevisionPattern.IsMatch(revision))
            || compatibility.SourceRevisions.Distinct(StringComparer.Ordinal).Count() != compatibility.SourceRevisions.Length
            || (compatibility.MinimumVersion is null) != (compatibility.MaximumExclusiveVersion is null)
            || (compatibility.MinimumVersion is not null
                && (!IsVersion(compatibility.MinimumVersion)
                    || !IsVersion(compatibility.MaximumExclusiveVersion)
                    || CompareVersions(compatibility.MinimumVersion, compatibility.MaximumExclusiveVersion!) >= 0))
            || (compatibility.MinimumVersion is null && compatibility.SourceRevisions.Length == 0))
        {
            throw new InvalidOperationException("Server release metadata has invalid version, source or CLI compatibility fields.");
        }

        return metadata;
    }

    private static bool IsVersion(string? value) =>
        value is not null && VersionPattern.IsMatch(value)
        && (!value.Contains('-') || value[(value.IndexOf('-') + 1)..].Split('.')
            .All(part => !part.All(char.IsAsciiDigit) || part.Length == 1 || part[0] != '0'));

    private static int CompareVersions(string left, string right)
    {
        var a = left.Split('-', 2);
        var b = right.Split('-', 2);
        var aCore = a[0].Split('.');
        var bCore = b[0].Split('.');
        for (var index = 0; index < 3; index++)
        {
            var comparison = BigInteger.Parse(aCore[index]).CompareTo(BigInteger.Parse(bCore[index]));
            if (comparison != 0)
                return comparison;
        }

        if (a.Length != b.Length)
            return a.Length == 1 ? 1 : -1;
        if (a.Length == 1)
            return 0;
        var aPre = a[1].Split('.');
        var bPre = b[1].Split('.');
        for (var index = 0; index < Math.Min(aPre.Length, bPre.Length); index++)
        {
            var aNumeric = aPre[index].All(char.IsAsciiDigit);
            var bNumeric = bPre[index].All(char.IsAsciiDigit);
            var comparison = aNumeric && bNumeric
                ? BigInteger.Parse(aPre[index]).CompareTo(BigInteger.Parse(bPre[index]))
                : aNumeric != bNumeric ? (aNumeric ? -1 : 1)
                : string.CompareOrdinal(aPre[index], bPre[index]);
            if (comparison != 0)
                return comparison;
        }
        return aPre.Length.CompareTo(bPre.Length);
    }
}
