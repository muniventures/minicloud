using Minicloud.Worker.Pipeline;

namespace Minicloud.Worker.Tests.Pipeline;

public sealed class DeploymentBuildCachePolicyTests
{
    [Theory]
    [InlineData(268435456L, 1, 268435456L, 1, true)]
    [InlineData(17179869184L, 720, 17179869184L, 50, true)]
    [InlineData(0L, 168, 2147483648L, 10, false)]
    [InlineData(2147483648L, 721, 2147483648L, 10, false)]
    [InlineData(2147483648L, 168, 0L, 10, false)]
    [InlineData(2147483648L, 168, 2147483648L, 51, false)]
    public void BuildCachePolicyRejectsUnsafeLimits(long maxBytes, int ageHours, long freeBytes, int freePercent, bool valid)
    {
        var options = new DeploymentPipelineOptions
        {
            BuildCacheMaxBytes = maxBytes,
            BuildCacheMaxAgeHours = ageHours,
            BuildCacheMinFreeBytes = freeBytes,
            BuildCacheMinFreePercent = freePercent,
        };
        Assert.Equal(valid, options.HasValidBuildCachePolicy);
    }
}
