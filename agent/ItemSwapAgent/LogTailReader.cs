using System.Text;

namespace ItemSwapAgent;

/// <summary>
/// Tails kcd.log from its CURRENT end (never replays history - matches the
/// reference project's own log-tail transport convention) and raises
/// <see cref="LineRead"/> for each complete new line.
///
/// Handles edge cases confirmed to matter live:
///   - A restarted game process truncates/recreates the log file; detected
///     by the file shrinking, and handled by resuming from the new current
///     end rather than erroring or replaying.
///   - FileInfo.Length can report a stale, lagging size for a file another
///     process is actively appending to (a Windows metadata-caching gotcha)
///     - length is always read from an actually-opened FileStream instead.
///   - A single line's content and its trailing '\n' can arrive as two
///     separate writes: a read can capture "...content" with no newline
///     yet, and if nothing else gets written afterward, waiting forever for
///     a newline that a *later* line would have supplied means the line
///     never fires at all. Confirmed live: a lone drop with no further log
///     activity right after it sat in the "incomplete" buffer indefinitely.
///     Fixed by flushing a non-empty partial line anyway once it has gone
///     quiet (unchanged) for <see cref="PartialLineFlushDelay"/> - long
///     enough to still coalesce genuinely-still-writing content, short
///     enough not to meaningfully delay detection.
/// </summary>
public sealed class LogTailReader : IAsyncDisposable
{
    private static readonly TimeSpan PartialLineFlushDelay = TimeSpan.FromMilliseconds(400);

    private readonly string _path;
    private readonly TimeSpan _pollInterval;
    private long _position;
    private string _partialLine = "";
    private DateTime _partialLineLastActivityUtc;
    private readonly CancellationTokenSource _cts = new();
    private Task? _loopTask;

    public event Action<string>? LineRead;

    public LogTailReader(string path, TimeSpan? pollInterval = null)
    {
        _path = path;
        _pollInterval = pollInterval ?? TimeSpan.FromMilliseconds(250);
    }

    public void Start() => _loopTask = Task.Run(() => LoopAsync(_cts.Token));

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && !File.Exists(_path))
        {
            Console.WriteLine($"[logtail] waiting for {_path} to exist (is the game running?)");
            await Task.Delay(_pollInterval, ct).ConfigureAwait(false);
        }
        if (ct.IsCancellationRequested) return;

        try { _position = new FileInfo(_path).Length; }
        catch { _position = 0; }
        Console.WriteLine($"[logtail] tailing {_path} from byte {_position}");

        while (!ct.IsCancellationRequested)
        {
            try { await PollOnceAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Console.WriteLine($"[logtail] poll error: {ex.Message}"); }

            try { await Task.Delay(_pollInterval, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
    }

    private async Task PollOnceAsync(CancellationToken ct)
    {
        if (!File.Exists(_path)) return;

        using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var length = stream.Length;

        if (length < _position)
        {
            Console.WriteLine("[logtail] log file shrank (game restarted?) - resuming from its new end, not replaying");
            _position = 0;
            _partialLine = "";
        }

        if (length == _position)
        {
            FlushStalePartialLine();
            return;
        }

        stream.Seek(_position, SeekOrigin.Begin);
        var toRead = (int)Math.Min(length - _position, 1024 * 1024); // cap one read; the next poll picks up the rest
        var buffer = new byte[toRead];
        var read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false);
        if (read <= 0) return;
        _position += read;

        var text = _partialLine + Encoding.UTF8.GetString(buffer, 0, read);
        var lines = text.Split('\n');
        _partialLine = lines[^1]; // no trailing newline yet (or empty) - carried forward
        _partialLineLastActivityUtc = DateTime.UtcNow;
        for (var i = 0; i < lines.Length - 1; i++)
        {
            var line = lines[i].TrimEnd('\r');
            if (line.Length > 0) LineRead?.Invoke(line);
        }

        FlushStalePartialLine();
    }

    private void FlushStalePartialLine()
    {
        if (_partialLine.Length == 0) return;
        if (DateTime.UtcNow - _partialLineLastActivityUtc < PartialLineFlushDelay) return;

        var line = _partialLine.TrimEnd('\r');
        _partialLine = "";
        if (line.Length > 0) LineRead?.Invoke(line);
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        if (_loopTask is not null)
        {
            try { await _loopTask.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
        _cts.Dispose();
    }
}
