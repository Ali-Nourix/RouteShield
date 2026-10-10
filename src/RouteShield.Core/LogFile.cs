using System.Text;

namespace RouteShield;

/// <summary>
/// One append-only log file, held open for the session and written through a buffer.
///
/// Opening, appending to and closing the file for every line cost a file open and a scan by the
/// virus checker per line. At a thousand lines a second the thread reading the core's output fell
/// behind, and a core whose output is not read in time stops accepting connections. Lines now
/// land in memory and reach the disk when <see cref="Flush"/> runs, once a second. The file is
/// shared for reading, so the diagnostics export can read it while it is open, and it is moved
/// aside once it grows past <see cref="DefaultMaxBytes"/>, one previous file kept next to it.
/// </summary>
internal sealed class LogFile
{
    public const long DefaultMaxBytes = 4 * 1024 * 1024;

    /// <summary>After the file could not be written, how long to leave it before trying again.</summary>
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(30);

    private readonly object _gate = new();
    private readonly long _maxBytes;
    private StreamWriter? _writer;
    private long _length;
    private bool _dirty;
    private DateTimeOffset _retryAfter;

    public LogFile(string path, long maxBytes = DefaultMaxBytes)
    {
        _maxBytes = maxBytes;
        FilePath = path;
        PreviousPath = Path.Combine(
            Path.GetDirectoryName(path) ?? string.Empty,
            Path.GetFileNameWithoutExtension(path) + ".previous" + Path.GetExtension(path));
    }

    public string FilePath { get; }

    /// <summary>Where the last rotation moved the file; the diagnostics export reads it too.</summary>
    public string PreviousPath { get; }

    public void WriteLine(string line)
    {
        lock (_gate)
        {
            try
            {
                if (Open() is not { } writer)
                {
                    return;
                }

                writer.WriteLine(line);
                _length += Encoding.UTF8.GetByteCount(line) + Environment.NewLine.Length;
                _dirty = true;

                if (_length > _maxBytes)
                {
                    Rotate();
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                Abandon();
            }
        }
    }

    public void Flush()
    {
        lock (_gate)
        {
            if (!_dirty || _writer is null)
            {
                return;
            }

            try
            {
                _writer.Flush();
                _dirty = false;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                Abandon();
            }
        }
    }

    private StreamWriter? Open()
    {
        if (_writer is not null)
        {
            return _writer;
        }

        if (DateTimeOffset.UtcNow < _retryAfter)
        {
            return null;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        if (File.Exists(FilePath) && new FileInfo(FilePath).Length > _maxBytes)
        {
            MoveAside();
        }

        var stream = new FileStream(FilePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete, 4096);
        _length = stream.Length;
        _writer = new StreamWriter(stream, new UTF8Encoding(false), 64 * 1024) { AutoFlush = false };
        return _writer;
    }

    private void Rotate()
    {
        _writer!.Dispose();
        _writer = null;
        _dirty = false;
        MoveAside();
        _length = 0;
    }

    private void MoveAside()
    {
        try
        {
            File.Move(FilePath, PreviousPath, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Something has the file open without sharing deletion; keep appending and try again
            // after another full file rather than on every line.
        }
    }

    private void Abandon()
    {
        try
        {
            _writer?.Dispose();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }

        _writer = null;
        _dirty = false;
        _retryAfter = DateTimeOffset.UtcNow + RetryDelay;
    }
}
