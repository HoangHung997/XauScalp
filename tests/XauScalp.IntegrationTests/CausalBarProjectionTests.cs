using System.Runtime.CompilerServices;
using XauScalp.Domain;
using XauScalp.MarketData;

namespace XauScalp.IntegrationTests;

public sealed class CausalBarProjectionTests
{
    [Fact]
    public async Task Projection_UsesOnlyTicks_AndMatchesDirectAggregatorSemantics()
    {
        DateTimeOffset firstTime = new(2026, 9, 19, 12, 0, 59, TimeSpan.Zero);
        DateTimeOffset secondTime = new(2026, 9, 19, 12, 1, 0, TimeSpan.Zero);

        MarketEvent[] input =
        [
            new ConnectionStatusEvent(
                ContractVersions.MarketEventV1,
                firstTime,
                null,
                1,
                "mt5",
                "XAUUSD",
                "XAUUSD.G",
                MarketConnectionState.Connected,
                "test"),
            Tick(2, firstTime, 100m),
            Tick(3, secondTime, 101m),
        ];

        var projection = new CausalBarProjection(
            new CausalBarAggregator(
                new MarketClockNormalizer(BrokerClockConfiguration.UtcV1),
                "bars-test"));

        var output = new List<BarEvent>();
        await foreach (BarEvent barEvent in projection.ProjectAsync(AsAsync(input)))
        {
            output.Add(barEvent);
        }

        Assert.Equal(12, output.Count);
        Assert.DoesNotContain(output, static item => item.SequenceId == 1);
        Assert.Contains(
            output,
            static item => item.UpdateKind == BarUpdateKind.Closed
                && item.Bar.Timeframe == BarTimeframe.M1);
    }

    private static TickEvent Tick(long sequence, DateTimeOffset timestamp, decimal bid)
    {
        return new TickEvent(
            ContractVersions.MarketEventV1,
            timestamp,
            timestamp,
            sequence,
            "mt5",
            "XAUUSD",
            "XAUUSD.G",
            bid,
            bid + 0.20m,
            null,
            0,
            TickFlags.Bid | TickFlags.Ask);
    }

    private static async IAsyncEnumerable<MarketEvent> AsAsync(
        IEnumerable<MarketEvent> events,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.Yield();

        foreach (MarketEvent marketEvent in events)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return marketEvent;
        }
    }
}
