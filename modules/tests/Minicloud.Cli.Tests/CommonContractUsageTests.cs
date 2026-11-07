// SPDX-License-Identifier: MPL-2.0

using Minicloud.Cli.Api;

namespace Minicloud.Tests;

public sealed class CommonContractUsageTests
{
    [Fact]
    public void CliApiNamespaceContainsNoLocalWireContracts()
    {
        var allowedTypes = new HashSet<Type>
        {
            typeof(MinicloudApiClient),
            typeof(ApiException)
        };

        var localWireTypes = typeof(MinicloudApiClient).Assembly.ExportedTypes
            .Where(type => type.Namespace == typeof(MinicloudApiClient).Namespace)
            .Where(type => !allowedTypes.Contains(type))
            .Select(type => type.FullName)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Empty(localWireTypes);
    }

    [Fact]
    public void ApiClientPublicSurfaceUsesNoCliLocalContractTypes()
    {
        var cliAssembly = typeof(MinicloudApiClient).Assembly;
        var localContractTypes = typeof(MinicloudApiClient).GetMethods()
            .Where(method => method.DeclaringType == typeof(MinicloudApiClient))
            .SelectMany(method => method.GetParameters().Select(parameter => parameter.ParameterType)
                .Append(method.ReturnType))
            .SelectMany(LeafTypes)
            .Where(type => type.Assembly == cliAssembly)
            .Select(type => type.FullName)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Empty(localContractTypes);
    }

    private static IEnumerable<Type> LeafTypes(Type type)
    {
        if (!type.IsGenericType)
        {
            yield return type;
            yield break;
        }

        foreach (var argument in type.GetGenericArguments())
        {
            foreach (var leaf in LeafTypes(argument))
            {
                yield return leaf;
            }
        }
    }
}
