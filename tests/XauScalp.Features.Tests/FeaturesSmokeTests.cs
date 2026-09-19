using XauScalp.Features;

namespace XauScalp.Features.Tests;

public sealed class FeaturesSmokeTests
{
    [Fact]
    public void FeaturesProject_Loads()
    {
        Assert.Equal("Features", FeaturesAssemblyMarker.ComponentName);
    }
}
