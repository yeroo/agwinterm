using System.Diagnostics;

namespace Agwinterm.Core;

/// <summary>
/// Publishes small text files (the per-window tree, the window index) without blocking the thread
/// that produced them.
///
/// <para>The UI thread used to write the state file inline on every tree change: a snapshot, a
/// <c>WriteAllText</c>, a <c>File.Move</c>. On a machine whose endpoint-security filter holds a
/// rename for 20–50 s — then, once, for twenty minutes — every keystroke queued behind that call and
/// the window was flagged as hung (agliteterm #94, agwinterm #294). The snapshot is cheap and stays
/// where the data is; the I/O is what moves. A producer calls <see cref="Enqueue"/> with the
/// finished bytes and returns at once; a single background thread lets a burst settle and writes
/// the newest snapshot per path, once.</para>
///
/// <para>Three rules keep that from losing or misordering a save, and each has a test:</para>
/// <list type="bullet">
/// <item><b>Newest wins.</b> Every snapshot takes a stamp from one clock. A write whose stamp is
/// below the path's last published stamp is dropped: the file already holds something newer. So a
/// synchronous <see cref="Publish"/> (quit, a verb whose reply claims a checkpoint on disk) can
/// never be overwritten by an older snapshot still waiting in the queue.</item>
/// <item><b>Unchanged bytes are not rewritten.</b> A snapshot equal to the last published text,
/// while that file still exists, costs one existence probe and no write — and it still advances
/// the published stamp, exactly as a write would, so an older snapshot behind it is fenced out the
/// same way (the bug lite's first cut had).</item>
/// <item><b>A snapshot that must be on disk is written on the caller's thread.</b>
/// <see cref="Publish"/> writes now and reports why when it could not; it shares the fence and the
/// per-path write lock with the worker, so it waits for a write in flight rather than racing it.</item>
/// </list>
///
/// <para>Writes are <c>path.tmp</c> then <c>File.Move(overwrite)</c>, as before, so a crash never
/// leaves a truncated file. If the worker thread cannot start, <see cref="Enqueue"/> writes inline —
/// persistence never silently stops.</para>
/// </summary>
public sealed class StateWriter
{
    private sealed class Slot
    {
        public long Stamp;              // the newest snapshot taken for this path (under _lock)
        public string? Pending;         // that snapshot's text while it waits for the worker (under _lock)
        public long Published;          // the stamp of the text on disk, or skipped as equal (under WriteLock)
        public string? PublishedText;   // its bytes; null = nothing published by this process yet
        public readonly object WriteLock = new();
    }

    private readonly Dictionary<string, Slot> _slots = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();
    private readonly AutoResetEvent _wake = new(false);
    private readonly TimeSpan _settle;
    private readonly Action<string>? _log;
    private readonly Thread? _worker;
    private volatile bool _stop;
    private long _clock;

    /// <param name="settle">How long the worker lets a burst of snapshots settle before writing the
    /// newest of them. Zero writes as soon as it wakes.</param>
    /// <param name="log">Where a background write's failure is reported (a synchronous one is
    /// reported to its caller). Null drops them.</param>
    public StateWriter(TimeSpan settle, Action<string>? log = null)
    {
        _settle = settle;
        _log = log;
        try
        {
            _worker = new Thread(Run) { IsBackground = true, Name = "agwinterm state writer" };
            _worker.Start();
        }
        catch (Exception ex)
        {
            _worker = null;
            _log?.Invoke($"state writer thread could not start ({ex.Message}); state files are written inline");
        }
    }

    /// <summary>Snapshots taken for which no write has been attempted yet (tests).</summary>
    public int PendingCount { get { lock (_lock) return _slots.Values.Count(s => s.Pending is not null); } }

    private Slot SlotFor(string path)
    {
        if (!_slots.TryGetValue(path, out var s)) _slots[path] = s = new Slot();
        return s;
    }

    /// <summary>Hand the newest snapshot of <paramref name="path"/> to the worker and return. A
    /// snapshot still waiting is replaced, not queued behind. Without a worker this writes inline.</summary>
    public void Enqueue(string path, string text)
    {
        if (_worker is null || !_worker.IsAlive) { Publish(path, text, out _); return; }
        lock (_lock)
        {
            var s = SlotFor(path);
            s.Stamp = ++_clock;
            s.Pending = text;
        }
        _wake.Set();
    }

    /// <summary>Write <paramref name="text"/> to <paramref name="path"/> now, on this thread. Returns
    /// false with <paramref name="why"/> when the bytes did not reach the file; true when they did, or
    /// when the file already holds them, or when a newer snapshot has already been published.</summary>
    public bool Publish(string path, string text, out string? why)
    {
        Slot s; long stamp;
        lock (_lock) { s = SlotFor(path); stamp = s.Stamp = ++_clock; s.Pending = null; }
        return Write(s, path, text, stamp, out why);
    }

    /// <summary>The file was removed (or replaced) by something other than this writer: forget what it
    /// held, so the next snapshot with the same bytes is written rather than skipped.</summary>
    public void Forget(string path)
    {
        Slot s;
        lock (_lock) s = SlotFor(path);
        lock (s.WriteLock) s.PublishedText = null;
    }

    /// <summary>Stop the worker: it writes whatever is still pending (an older snapshot than a
    /// synchronous publish is fenced, so this cannot go backwards) and exits. Bounded by
    /// <paramref name="wait"/>; a worker stuck in the filesystem past that is left to die with the
    /// process, which is what it would have done anyway.</summary>
    public void Shutdown(TimeSpan wait)
    {
        _stop = true;
        _wake.Set();
        if (_worker is { IsAlive: true } && !_worker.Join(wait))
            _log?.Invoke($"state writer did not finish within {wait.TotalSeconds:0} s of shutdown");
    }

    private void Run()
    {
        while (!_stop)
        {
            _wake.WaitOne();
            if (_stop) break;
            if (_settle > TimeSpan.Zero) Thread.Sleep(_settle);   // a burst becomes one write
            Drain();
        }
        Drain();
    }

    private void Drain()
    {
        List<(string path, Slot slot, string text, long stamp)> work;
        lock (_lock)
        {
            work = _slots.Where(kv => kv.Value.Pending is not null)
                         .Select(kv => (kv.Key, kv.Value, kv.Value.Pending!, kv.Value.Stamp)).ToList();
            foreach (var (_, slot, _, _) in work) slot.Pending = null;
        }
        foreach (var (path, slot, text, stamp) in work)
            if (!Write(slot, path, text, stamp, out var why)) _log?.Invoke($"state save to {path} failed: {why}");
    }

    private static bool Write(Slot s, string path, string text, long stamp, out string? why)
    {
        why = null;
        lock (s.WriteLock)
        {
            if (stamp < s.Published) return true;   // overtaken: the file already holds a newer snapshot
            // Nothing to write — and the fence moves anyway: this snapshot IS what the file holds, so
            // an older one still waiting must be dropped by it exactly as if it had been written.
            if (s.PublishedText is not null && s.PublishedText == text && File.Exists(path))
            {
                s.Published = stamp;
                return true;
            }
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                string tmp = path + ".tmp";
                File.WriteAllText(tmp, text);
                File.Move(tmp, path, overwrite: true);   // atomic replace: a crash never leaves a truncated file
                s.Published = stamp;
                s.PublishedText = text;
                return true;
            }
            catch (Exception ex)
            {
                // Whatever the file holds now, it is not known to be these bytes: a later save with
                // them must write, not skip.
                s.PublishedText = null;
                why = ex.Message;
                return false;
            }
        }
    }
}
