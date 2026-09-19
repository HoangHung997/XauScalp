using XauScalp.Risk;

namespace XauScalp.Risk.Tests;

public sealed class RiskSmokeTests
{
    [Fact]
    public void RiskProject_Loads()
    {
        Assert.Equal("Risk", RiskAssemblyMarker.ComponentName);
    }
}
