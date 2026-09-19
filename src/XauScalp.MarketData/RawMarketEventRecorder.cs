using XauScalp.Domain;

namespace XauScalp.MarketData;

public sealed class RawMarketEventRecorder
{
    public async Task<long> RecordAsync(
        IMarketDataSource source,
        IRawMarketEventSink sink,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(sink);

        long count = 0;
        await foreach (MarketEvent marketEvent in source.ReadEventsAsync(cancellationToken).ConfigureAwait(false))
        {
            await sink.AppendAsync(marketEvent, cancellationToken).ConfigureAwait(false);
            count = checked(count + 1);
        }

        return count;
    }
}
