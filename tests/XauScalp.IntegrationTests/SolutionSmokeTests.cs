using XauScalp.DecisionModels;
using XauScalp.Domain;
using XauScalp.Execution;
using XauScalp.Features;
using XauScalp.MarketData;
using XauScalp.Persistence;
using XauScalp.Replay;
using XauScalp.Risk;

namespace XauScalp.IntegrationTests;

public sealed class SolutionSmokeTests
{
    [Fact]
    public void CoreProjects_AreLoadable()
    {
        string[] components =
        [
            DomainAssemblyMarker.ComponentName,
            MarketDataAssemblyMarker.ComponentName,
            FeaturesAssemblyMarker.ComponentName,
            DecisionModelsAssemblyMarker.ComponentName,
            RiskAssemblyMarker.ComponentName,
            ExecutionAssemblyMarker.ComponentName,
            PersistenceAssemblyMarker.ComponentName,
            ReplayAssemblyMarker.ComponentName,
        ];

        Assert.Equal(8, components.Distinct(StringComparer.Ordinal).Count());
    }
}
