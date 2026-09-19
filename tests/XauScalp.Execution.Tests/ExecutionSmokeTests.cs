using XauScalp.Execution;

namespace XauScalp.Execution.Tests;

public sealed class ExecutionSmokeTests
{
    [Fact]
    public void ExecutionProject_Loads()
    {
        Assert.Equal("Execution", ExecutionAssemblyMarker.ComponentName);
    }
}
