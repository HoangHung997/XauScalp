using XauScalp.Replay;

namespace XauScalp.Replay.Tests;

public sealed class ReplaySmokeTests
{
    [Fact]
    public void ReplayProject_Loads()
    {
        Assert.Equal("Replay", ReplayAssemblyMarker.ComponentName);
    }
}
