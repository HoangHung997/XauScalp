using System.Diagnostics;
using XauScalp.Domain;

namespace XauScalp.DecisionModels;

public sealed record NativeInferenceBenchmarkResult(
    int Iterations,
    TimeSpan Average,
    TimeSpan P50,
    TimeSpan P95,
    TimeSpan Maximum);

public static class NativeInferenceBenchmark
{
    public static async Task<NativeInferenceBenchmarkResult> MeasureAsync(
        IXauDecisionModel model,
        XauMarketState state,
        int iterations,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(state);

        if (iterations <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(iterations),
                iterations,
                "Benchmark iterations must be positive.");
        }

        long[] elapsedTicks = new long[iterations];

        for (int index = 0; index < iterations; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            long started = Stopwatch.GetTimestamp();

            _ = await model
                .EvaluateAsync(state, cancellationToken)
                .ConfigureAwait(false);

            TimeSpan elapsed = Stopwatch.GetElapsedTime(started);
            elapsedTicks[index] = elapsed.Ticks;
        }

        Array.Sort(elapsedTicks);

        return new NativeInferenceBenchmarkResult(
            iterations,
            TimeSpan.FromTicks(
                checked((long)elapsedTicks.Average())),
            TimeSpan.FromTicks(
                Percentile(elapsedTicks, 0.50)),
            TimeSpan.FromTicks(
                Percentile(elapsedTicks, 0.95)),
            TimeSpan.FromTicks(elapsedTicks[^1]));
    }

    private static long Percentile(long[] sortedTicks, double percentile)
    {
        int index = (int)Math.Ceiling(
            percentile * sortedTicks.Length) - 1;

        return sortedTicks[Math.Clamp(index, 0, sortedTicks.Length - 1)];
    }
}
