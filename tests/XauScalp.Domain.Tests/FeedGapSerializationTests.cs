using System.Text.Json;

namespace XauScalp.Domain.Tests;

public sealed class FeedGapSerializationTests
{
    [Fact]
    public void FeedGapEvent_RoundTripsThroughMarketEventContract()
    {
        MarketEvent original = new FeedGapEvent(
            ContractVersions.MarketEventV1,
            ContractTestFactory.UtcNow,
            sequenceId: 12,
            dataSourceId: "mt5-demo",
            symbol: "XAUUSD",
            brokerSymbol: "XAUUSD.G",
            expectedSequenceId: 11,
            observedSequenceId: 12,
            FeedSequenceAnomalyKind.MissingRange);

        JsonSerializerOptions options = XauJson.CreateOptions();
        string json = JsonSerializer.Serialize(original, options);
        MarketEvent? restored = JsonSerializer.Deserialize<MarketEvent>(json, options);

        FeedGapEvent gap = Assert.IsType<FeedGapEvent>(restored);
        Assert.Equal(11, gap.ExpectedSequenceId);
        Assert.Equal(12, gap.ObservedSequenceId);
        Assert.Equal(FeedSequenceAnomalyKind.MissingRange, gap.Kind);
    }
}
