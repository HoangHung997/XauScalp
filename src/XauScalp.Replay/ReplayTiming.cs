using System.Runtime.CompilerServices;
using XauScalp.Domain;

namespace XauScalp.Replay;

public enum ReplayTimingMode
{
    Step = 0,
    Realtime = 1,
    Accelerated = 2,
}

public sealed record ReplayTimingOptions
{
    public ReplayTimingOptions(ReplayTimingMode mode, double accelerationFactor = 1)
    {
        if (!double.IsFinite(accelerationFactor) || accelerationFactor <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(accelerationFactor),
                accelerationFactor,
                "Acceleration factor must be finite and positive.");
        }

        if (mode != ReplayTimingMode.Accelerated && Math.Abs(accelerationFactor - 1) > double.Epsilon)
        {
            throw new ArgumentException(
                "A non-default acceleration factor is only valid in Accelerated mode.",
                nameof(accelerationFactor));
        }

        Mode = mode;
        AccelerationFactor = accelerationFactor;
    }

    public ReplayTimingMode Mode { get; }

    public double AccelerationFactor { get; }

    public static ReplayTimingOptions Step { get; } = new(ReplayTimingMode.Step);

    public static ReplayTimingOptions Realtime { get; } = new(ReplayTimingMode.Realtime);

    public static ReplayTimingOptions Accelerated(double factor) =>
        new(ReplayTimingMode.Accelerated, factor);
}

public interface IReplayDelay
{
    ValueTask DelayAsync(TimeSpan delay, CancellationToken cancellationToken);
}

public sealed class SystemReplayDelay : IReplayDelay
{
    public ValueTask DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        if (delay <= TimeSpan.Zero)
        {
            return ValueTask.CompletedTask;
        }

        return new ValueTask(Task.Delay(delay, cancellationToken));
    }
}

public interface IReplayStepGate
{
    ValueTask WaitForNextAsync(CancellationToken cancellationToken);
}

public sealed class ReplayStepController : IReplayStepGate, IDisposable
{
    private readonly SemaphoreSlim _steps = new(0);
    private bool _disposed;

    public void Advance(int count = 1)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (count <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(count), count, "Advance count must be positive.");
        }

        _steps.Release(count);
    }

    public ValueTask WaitForNextAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return new ValueTask(_steps.WaitAsync(cancellationToken));
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _steps.Dispose();
        _disposed = true;
    }
}

public sealed class ReplayEventScheduler
{
    private readonly IReplayDelay _delay;

    public ReplayEventScheduler(IReplayDelay? delay = null)
    {
        _delay = delay ?? new SystemReplayDelay();
    }

    public async IAsyncEnumerable<MarketEvent> ScheduleAsync(
        IAsyncEnumerable<MarketEvent> source,
        ReplayTimingOptions timing,
        IReplayStepGate? stepGate = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(timing);

        if (timing.Mode == ReplayTimingMode.Step && stepGate is null)
        {
            throw new ArgumentException("Step replay requires a step gate.", nameof(stepGate));
        }

        DateTimeOffset? previousTimestampUtc = null;

        await foreach (MarketEvent marketEvent in source
            .WithCancellation(cancellationToken)
            .ConfigureAwait(false))
        {
            if (previousTimestampUtc is DateTimeOffset previous
                && marketEvent.TimestampUtc < previous)
            {
                throw new InvalidDataException(
                    "Replay source timestamp regressed. The scheduler never reorders source evidence.");
            }

            if (timing.Mode == ReplayTimingMode.Step)
            {
                await stepGate!.WaitForNextAsync(cancellationToken).ConfigureAwait(false);
            }
            else if (previousTimestampUtc is DateTimeOffset prior)
            {
                TimeSpan sourceDelay = marketEvent.TimestampUtc - prior;
                TimeSpan replayDelay = timing.Mode == ReplayTimingMode.Realtime
                    ? sourceDelay
                    : TimeSpan.FromTicks(
                        checked((long)Math.Round(
                            sourceDelay.Ticks / timing.AccelerationFactor,
                            MidpointRounding.AwayFromZero)));

                await _delay.DelayAsync(replayDelay, cancellationToken).ConfigureAwait(false);
            }

            previousTimestampUtc = marketEvent.TimestampUtc;
            yield return marketEvent;
        }
    }
}
