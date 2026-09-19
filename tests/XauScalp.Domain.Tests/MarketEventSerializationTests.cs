using System.Text.Json;

namespace XauScalp.Domain.Tests;

public sealed class MarketEventSerializationTests
{
    [Fact]
    public void TickEvent_RoundTripsThroughBaseContract()
    {
        MarketEvent original = new TickEvent(
            ContractVersions.MarketEventV1,
            ContractTestFactory.UtcNow,
            ContractTestFactory.UtcNow.AddHours(2),
            42,
            "mt5-demo",
            "XAUUSD",
            "XAUUSD.G",
            3680.10m,
            3680.30m,
            3680.20m,
            7,
            TickFlags.Bid | TickFlags.Ask | TickFlags.Last);

        JsonSerializerOptions options = XauJson.CreateOptions();
        string json = JsonSerializer.Serialize(original, options);
        MarketEvent? restored = JsonSerializer.Deserialize<MarketEvent>(json, options);

        TickEvent tick = Assert.IsType<TickEvent>(restored);
        Assert.Equal(42, tick.SequenceId);
        Assert.Equal(3680.10m, tick.Bid);
        Assert.Equal("XAUUSD.G", tick.BrokerSymbol);
        Assert.Equal(ContractVersions.MarketEventV1, tick.ContractVersion);
    }

    [Fact]
    public void ClosedBarEvent_RoundTripsWithExplicitClosedState()
    {
        var closed = new ClosedBarState(
            BarTimeframe.M1,
            ContractTestFactory.UtcNow,
            3680m,
            3682m,
            3679m,
            3681m,
            100,
            ContractTestFactory.UtcNow.AddMinutes(1));

        MarketEvent original = new BarEvent(
            ContractVersions.MarketEventV1,
            ContractTestFactory.UtcNow.AddMinutes(1),
            null,
            100,
            "replay",
            "XAUUSD",
            "XAUUSD",
            BarUpdateKind.Closed,
            closed);

        JsonSerializerOptions options = XauJson.CreateOptions();
        string json = JsonSerializer.Serialize(original, options);
        MarketEvent? restored = JsonSerializer.Deserialize<MarketEvent>(json, options);

        BarEvent barEvent = Assert.IsType<BarEvent>(restored);
        Assert.Equal(BarUpdateKind.Closed, barEvent.UpdateKind);
        Assert.IsType<ClosedBarState>(barEvent.Bar);
    }

    [Fact]
    public void ClosedEvent_RejectsFormingBarState()
    {
        var forming = new FormingBarState(
            BarTimeframe.M1,
            ContractTestFactory.UtcNow,
            3680m,
            3682m,
            3679m,
            3681m,
            10,
            ContractTestFactory.UtcNow.AddSeconds(15));

        Assert.Throws<ArgumentException>(
            () => new BarEvent(
                ContractVersions.MarketEventV1,
                ContractTestFactory.UtcNow.AddSeconds(15),
                null,
                10,
                "test",
                "XAUUSD",
                "XAUUSD",
                BarUpdateKind.Closed,
                forming));
    }
}
