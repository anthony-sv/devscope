using System.Text;

namespace Devscope.Core.Logs;

/// <summary>
/// Incrementally reads one log file (local or UNC), like <c>tail -F</c>.
/// The file is opened only for the duration of each poll and with full sharing, so the writing
/// application is never blocked and rotation (rename + recreate) or truncation is detected.
/// </summary>
public sealed class LogFileTailer
{
    private const int ChunkSize = 64 * 1024;

    private readonly long _initialTailBytes;
    private readonly LogEntryAssembler _assembler;
    private readonly StringBuilder _partialLine = new();
    private long _position = -1;
    private DateTime _creationTimeUtc;
    private Decoder? _decoder;
    private bool _skipFirstLine;

    /// <summary>Creates a tailer.</summary>
    /// <param name="source">The file and environment.</param>
    /// <param name="initialTailBytes">On the first poll, only the last N bytes are read (0 = whole file).</param>
    /// <param name="nextId">Shared id generator so ids are unique across files.</param>
    public LogFileTailer(LogFileSource source, long initialTailBytes, Func<long> nextId)
    {
        ArgumentNullException.ThrowIfNull(source);
        Source = source;
        _initialTailBytes = initialTailBytes;

        var fallbackDate = DateOnly.FromDateTime(File.Exists(source.FilePath) ? File.GetLastWriteTime(source.FilePath) : DateTime.Today);
        _assembler = new LogEntryAssembler(source, fallbackDate, nextId);
    }

    public LogFileSource Source { get; }

    /// <summary>Last error while reading (file missing, access denied, share offline), or null.</summary>
    public string? LastError { get; private set; }

    /// <summary>Bytes consumed so far.</summary>
    public long Position => Math.Max(_position, 0);

    /// <summary>Reads everything appended since the last poll and returns the completed events.</summary>
    public async Task<IReadOnlyList<LogEntry>> PollAsync(CancellationToken cancellationToken)
    {
        var output = new List<LogEntry>();
        try
        {
            await using var stream = new FileStream(Source.FilePath, new FileStreamOptions
            {
                Mode = FileMode.Open,
                Access = FileAccess.Read,
                Share = FileShare.ReadWrite | FileShare.Delete,
                Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
                BufferSize = 0,
            });

            var length = stream.Length;
            var creation = File.GetCreationTimeUtc(Source.FilePath);
            if (_position < 0)
            {
                await StartAsync(stream, length, cancellationToken).ConfigureAwait(false);
                _creationTimeUtc = creation;
            }
            else if (length < _position || creation != _creationTimeUtc)
            {
                // Truncated or replaced by rotation: start over from the beginning of the new file.
                _assembler.Reset();
                _partialLine.Clear();
                _decoder = null;
                _position = 0;
                _skipFirstLine = false;
                _creationTimeUtc = creation;
            }

            if (length == _position)
            {
                // Quiet file: release buffered content. A final line without a newline is emitted too,
                // otherwise the last line of a finished file would never show up.
                if (_partialLine.Length > 0 && !_skipFirstLine)
                {
                    _assembler.Add(_partialLine.ToString().TrimEnd('\r'), output);
                    _partialLine.Clear();
                }

                _assembler.Flush(output);
                LastError = null;
                return output;
            }

            stream.Position = _position;
            _decoder ??= Encoding.UTF8.GetDecoder();
            var bytes = new byte[ChunkSize];
            var chars = new char[Encoding.UTF8.GetMaxCharCount(ChunkSize)];
            int read;
            while ((read = await stream.ReadAsync(bytes, cancellationToken).ConfigureAwait(false)) > 0)
            {
                var charCount = _decoder.GetChars(bytes, 0, read, chars, 0);
                Consume(chars.AsSpan(0, charCount), output);
                _position += read;
            }

            LastError = null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LastError = ex.Message;
        }

        return output;
    }

    private async Task StartAsync(FileStream stream, long length, CancellationToken cancellationToken)
    {
        var encoding = await DetectEncodingAsync(stream, cancellationToken).ConfigureAwait(false);
        _decoder = encoding.Encoding.GetDecoder();

        var start = _initialTailBytes > 0 ? Math.Max(encoding.PreambleLength, length - _initialTailBytes) : encoding.PreambleLength;
        if (encoding.Encoding is UnicodeEncoding && (start - encoding.PreambleLength) % 2 != 0)
        {
            start++;
        }

        _skipFirstLine = start > encoding.PreambleLength;
        _position = start;
    }

    private void Consume(ReadOnlySpan<char> chars, List<LogEntry> output)
    {
        while (!chars.IsEmpty)
        {
            var newline = chars.IndexOf('\n');
            if (newline < 0)
            {
                _partialLine.Append(chars);
                return;
            }

            _partialLine.Append(chars[..newline]);
            chars = chars[(newline + 1)..];

            if (_skipFirstLine)
            {
                // We started mid-file: the first line is a fragment.
                _skipFirstLine = false;
                _partialLine.Clear();
                continue;
            }

            if (_partialLine.Length > 0 && _partialLine[^1] == '\r')
            {
                _partialLine.Length--;
            }

            _assembler.Add(_partialLine.ToString(), output);
            _partialLine.Clear();
        }
    }

    private static async Task<(Encoding Encoding, int PreambleLength)> DetectEncodingAsync(FileStream stream, CancellationToken cancellationToken)
    {
        var bom = new byte[3];
        stream.Position = 0;
        var read = await stream.ReadAtLeastAsync(bom, bom.Length, throwOnEndOfStream: false, cancellationToken).ConfigureAwait(false);
        return bom.AsSpan(0, read) switch
        {
            [0xEF, 0xBB, 0xBF] => (new UTF8Encoding(false), 3),
            [0xFF, 0xFE, ..] => (Encoding.Unicode, 2),
            [0xFE, 0xFF, ..] => (Encoding.BigEndianUnicode, 2),
            _ => (new UTF8Encoding(false), 0),
        };
    }
}
