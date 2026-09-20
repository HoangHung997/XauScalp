using System.Text;
using System.Text.Json;
using XauScalp.Domain;

namespace XauScalp.Replay;

public static class ResearchResultExporter
{
    public static Task WriteFirstPassageLabelsJsonlAsync(
        Stream stream,
        IEnumerable<FirstPassageStateLabels> labels,
        CancellationToken cancellationToken = default)
    {
        return WriteJsonlAsync(stream, labels, cancellationToken);
    }

    public static Task WriteEntryOutcomesJsonlAsync(
        Stream stream,
        IEnumerable<EntryTradeOutcome> outcomes,
        CancellationToken cancellationToken = default)
    {
        return WriteJsonlAsync(stream, outcomes, cancellationToken);
    }

    public static Task WriteEntrySummariesJsonlAsync(
        Stream stream,
        IEnumerable<EntryModeSummary> summaries,
        CancellationToken cancellationToken = default)
    {
        return WriteJsonlAsync(stream, summaries, cancellationToken);
    }

    private static async Task WriteJsonlAsync<T>(
        Stream stream,
        IEnumerable<T> records,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(records);

        if (!stream.CanWrite)
        {
            throw new ArgumentException("Research export stream must be writable.", nameof(stream));
        }

        JsonSerializerOptions options = XauJson.CreateOptions();
        await using var writer = new StreamWriter(
            stream,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            bufferSize: 64 * 1024,
            leaveOpen: true);

        foreach (T record in records)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string json = JsonSerializer.Serialize(record, options);
            await writer.WriteLineAsync(
                json.AsMemory(),
                cancellationToken).ConfigureAwait(false);
        }

        await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
    }
}
