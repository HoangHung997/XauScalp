using XauScalp.Domain;
using XauScalp.Features;

namespace XauScalp.Replay;

public sealed class DeterministicReplayRunner
{
    private readonly Func<IXauFeatureEngine> _featureEngineFactory;
    private readonly ReplayEventScheduler _scheduler;

    public DeterministicReplayRunner(
        Func<IXauFeatureEngine> featureEngineFactory,
        ReplayEventScheduler? scheduler = null)
    {
        _featureEngineFactory = featureEngineFactory
            ?? throw new ArgumentNullException(nameof(featureEngineFactory));
        _scheduler = scheduler ?? new ReplayEventScheduler();
    }

    public async Task<ReplayRunResult> RunAsync(
        IAsyncEnumerable<MarketEvent> source,
        ReplayRunOptions options,
        IReplayRecordSink recordSink,
        IReplayFeatureContextProvider? featureContextProvider = null,
        IReplayStepGate? stepGate = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(recordSink);

        IXauFeatureEngine featureEngine = _featureEngineFactory()
            ?? throw new InvalidOperationException("Feature engine factory returned null.");

        using var dataSetHasher = new CanonicalJsonLineHasher();
        using var outputHasher = new CanonicalJsonLineHasher();

        long eventCount = 0;
        long tickCount = 0;
        DateTimeOffset? startTimestampUtc = null;
        DateTimeOffset? endTimestampUtc = null;

        await foreach (MarketEvent marketEvent in _scheduler
            .ScheduleAsync(source, options.Timing, stepGate, cancellationToken)
            .ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();

            startTimestampUtc ??= marketEvent.TimestampUtc;
            endTimestampUtc = marketEvent.TimestampUtc;
            dataSetHasher.AppendMarketEvent(marketEvent);

            XauMarketState? featureState = null;

            if (marketEvent is TickEvent)
            {
                FeatureExternalContext? externalContext = featureContextProvider?.GetContext(
                    marketEvent,
                    options.CostScenario);

                featureEngine.SetExternalContext(externalContext);
                featureState = featureEngine.Update(marketEvent);
                tickCount++;
            }
            else
            {
                featureEngine.ObserveContext(marketEvent);
            }

            var outputRecord = new ReplayOutputRecord(
                eventCount,
                marketEvent,
                featureState,
                options.CostScenario);

            outputHasher.AppendOutputRecord(outputRecord);
            await recordSink
                .WriteAsync(outputRecord, cancellationToken)
                .ConfigureAwait(false);

            eventCount++;
        }

        if (eventCount == 0
            || startTimestampUtc is not DateTimeOffset start
            || endTimestampUtc is not DateTimeOffset end)
        {
            throw new InvalidDataException("Replay dataset is empty.");
        }

        await recordSink.CompleteAsync(cancellationToken).ConfigureAwait(false);

        string dataSetSha256 = dataSetHasher.Complete();
        string outputSha256 = outputHasher.Complete();

        ReplayRunManifest manifest = ReplayManifestFactory.Create(
            options,
            dataSetSha256,
            featureEngine.FeatureSchemaVersion,
            featureEngine.FeatureEngineVersion,
            start,
            end,
            eventCount,
            tickCount);

        return new ReplayRunResult(
            manifest,
            outputSha256,
            eventCount);
    }
}
