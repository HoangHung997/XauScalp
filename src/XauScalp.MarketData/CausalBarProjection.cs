using System.Runtime.CompilerServices;
using XauScalp.Domain;

namespace XauScalp.MarketData;

public sealed class CausalBarProjection
{
    private readonly CausalBarAggregator _aggregator;

    public CausalBarProjection(CausalBarAggregator aggregator)
    {
        _aggregator = aggregator ?? throw new ArgumentNullException(nameof(aggregator));
    }

    public async IAsyncEnumerable<BarEvent> ProjectAsync(
        IAsyncEnumerable<MarketEvent> marketEvents,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(marketEvents);

        await foreach (MarketEvent marketEvent in marketEvents.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            if (marketEvent is not TickEvent tick)
            {
                continue;
            }

            foreach (BarEvent barEvent in _aggregator.Apply(tick))
            {
                yield return barEvent;
            }
        }
    }
}
