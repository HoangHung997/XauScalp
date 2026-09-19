using System.Runtime.CompilerServices;
using System.Text;

namespace XauScalp.MarketData;

public sealed class Mt5NdjsonFileTransport : IMt5Transport
{
    private readonly string _path;
    private readonly bool _follow;
    private readonly TimeSpan _pollInterval;
    private readonly long _startAfterSourceSequenceId;

    public Mt5NdjsonFileTransport(
        string path,
        bool follow = true,
        TimeSpan? pollInterval = null,
        long startAfterSourceSequenceId = -1)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("Bridge file path is required.", nameof(path));
        }

        if (startAfterSourceSequenceId < -1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(startAfterSourceSequenceId),
                startAfterSourceSequenceId,
                "Resume sequence must be -1 or non-negative.");
        }

        TimeSpan interval = pollInterval ?? TimeSpan.FromMilliseconds(25);
        if (interval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(pollInterval), interval, "Poll interval must be positive.");
        }

        _path = Path.GetFullPath(path);
        _follow = follow;
        _pollInterval = interval;
        _startAfterSourceSequenceId = startAfterSourceSequenceId;
    }

    public async IAsyncEnumerable<Mt5WireMessage> ReadAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        while (!File.Exists(_path))
        {
            if (!_follow)
            {
                yield break;
            }

            await Task.Delay(_pollInterval, cancellationToken).ConfigureAwait(false);
        }

        await using var stream = new FileStream(
            _path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 64 * 1024,
            options: FileOptions.Asynchronous | FileOptions.SequentialScan);

        using var reader = new StreamReader(
            stream,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            detectEncodingFromByteOrderMarks: true,
            bufferSize: 64 * 1024,
            leaveOpen: false);

        char[] buffer = new char[16 * 1024];
        var pending = new StringBuilder();

        while (true)
        {
            int read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                if (!_follow)
                {
                    if (!string.IsNullOrWhiteSpace(pending.ToString()))
                    {
                        throw new InvalidDataException("MT5 bridge file ends with an incomplete NDJSON frame.");
                    }

                    yield break;
                }

                await Task.Delay(_pollInterval, cancellationToken).ConfigureAwait(false);
                continue;
            }

            pending.Append(buffer, 0, read);

            while (TryTakeLine(pending, out string? line))
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                Mt5WireMessage message = Mt5NdjsonParser.Parse(line);
                if (message.SourceSequenceId <= _startAfterSourceSequenceId)
                {
                    continue;
                }

                yield return message;
            }
        }
    }

    private static bool TryTakeLine(StringBuilder pending, out string? line)
    {
        for (int index = 0; index < pending.Length; index++)
        {
            if (pending[index] != '\n')
            {
                continue;
            }

            int contentLength = index;
            if (contentLength > 0 && pending[contentLength - 1] == '\r')
            {
                contentLength--;
            }

            line = pending.ToString(0, contentLength);
            pending.Remove(0, index + 1);
            return true;
        }

        line = null;
        return false;
    }
}
