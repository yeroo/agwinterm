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
/// <para>Three rules keep that from losing or misordering a save, and each has a test in
/// <c>StateWriterTests</c> that fails with its line removed:</para>
/// <list type="bullet">
/// <item><b>Newest wins, by the clock at snapshot time.</b> Every snapshot takes a stamp from one
/// clock when it is BUILT — <see cref="Enqueue"/> stamps as it queues, a synchronous caller stamps
/// with <see cref="Reserve"/> where it builds and publishes with that stamp later. A write whose
/// stamp is below the path's last published stamp is dropped: the file already holds something
/// newer. So a snapshot built on the UI thread and written on a pipe thread cannot land over a
/// newer one the UI thread queued in between.</item>
/// <item><b>Unchanged bytes are not rewritten.</b> A snapshot equal to the last published text,
/// while that file still exists, costs one existence probe and no write — and it still advances
/// the published stamp, exactly as a write would, so an older snapshot behind it is fenced out the
/// same way (the bug lite's first cut had).</item>
/// <item><b>A snapshot that must be on disk is written on the caller's thread.</b>
/// <see cref="Publish(string, string, long, out string?)"/> writes now and reports why when it
/// could not; it shares the fence and the per-path write lock with the worker, so it waits for a
/// write in flight rather than racing it.</item>
/// </list>
///
/// <para>Writes are <c>path.tmp</c> then <c>File.Move(overwrite)</c>, as before, so a crash never
/// leaves a truncated file. <see cref="Delete"/> removes a file under the same fence, so a snapshot
/// still queued cannot resurrect it. If the worker thread cannot start, <see cref="Enqueue"/>
/// writes inline — persistence never silently stops.</para>
/// </summary>
public sealed class StateWriter
{
    private sealed class Slot
    {
        public long PendingStamp;       // the stamp of Pending (under _lock)
        public string? Pending;         // a snapshot waiting for the worker (under _lock)
        public long Published;          // the stamp of the text on disk, or skipped as equal (under WriteLock)
        public string? PublishedText;   // its bytes; null = not known to be on disk
        public readonly object WriteLock = new();
    }

    private readonly Dictionary<string, Slot> _slots = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();
    private readonly AutoResetEvent _wake = new(false);
    private readonly ManualResetEvent _stopping = new(false);   // interrupts the settle, unlike an Enqueue
    private readonly TimeSpan _settle;
    private readonly Action<string>? _log;
    private readonly Thread? _worker;
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

    /// <summary>Snapshots queued for which no write has been attempted yet.</summary>
    internal int PendingCount { get { lock (_lock) return _slots.Values.Count(s => s.Pending is not null); } }

    /// <summary>How many times the worker has drained its queue (tests wait on it).</summary>
    internal long Drains => Interlocked.Read(ref _drains);
    private long _drains;

    /// <summary>Test seam: runs on the worker with each path it is about to write, after the snapshot
    /// left the queue and before the fence is checked — where a synchronous publish can overtake it.</summary>
    internal Action<string>? BeforeWrite;

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
            s.PendingStamp = ++_clock;
            s.Pending = text;
        }
        _wake.Set();
    }

    /// <summary>A stamp for a snapshot about to be built, taken where the tree is read so that its
    /// place in the order is the moment of the snapshot, not of the write. Pass it to
    /// <see cref="Publish(string, string, long, out string?)"/>.</summary>
    public long Reserve() { lock (_lock) return ++_clock; }

    /// <summary>Write <paramref name="text"/> now, on this thread, as a snapshot taken now.</summary>
    public bool Publish(string path, string text, out string? why) => Publish(path, text, Reserve(), out why);

    /// <summary>Write <paramref name="text"/> to <paramref name="path"/> now, on this thread, as the
    /// snapshot stamped <paramref name="stamp"/> by <see cref="Reserve"/>. A queued snapshot no newer
    /// than it is dropped; a newer one stays queued and is written by the worker in its turn. Returns
    /// false with <paramref name="why"/> when the bytes did not reach the file; true when they did, or
    /// when the file already holds them, or when a newer snapshot has already been published.</summary>
    public bool Publish(string path, string text, long stamp, out string? why)
    {
        Slot s;
        lock (_lock)
        {
            s = SlotFor(path);
            if (s.Pending is not null && s.PendingStamp <= stamp) s.Pending = null;
        }
        return Write(s, path, text, stamp, out why);
    }

    /// <summary>Remove the file, as the newest thing that happened to it: a snapshot still queued or
    /// in flight is older and cannot put it back. The file's bytes are forgotten, so the next
    /// snapshot with the same bytes is written rather than skipped — the file may have been recreated
    /// with other bytes in between. Missing already counts as removed; <paramref name="removed"/> says
    /// whether there was anything to remove, on disk or still queued, so a caller can tell "cleared"
    /// from "there was no state" without a File.Exists of its own that a queued snapshot would slip past.</summary>
    public bool Delete(string path, out string? why, out bool removed)
    {
        why = null;
        Slot s; long stamp; bool dropped;
        lock (_lock) { s = SlotFor(path); stamp = ++_clock; dropped = s.Pending is not null; s.Pending = null; }
        lock (s.WriteLock)
        {
            bool existed = File.Exists(path);
            try { File.Delete(path); }
            catch (Exception ex) { why = ex.Message; }
            s.Published = stamp;
            s.PublishedText = null;
            removed = existed || dropped;
            return why is null;
        }
    }

    /// <inheritdoc cref="Delete(string, out string?, out bool)"/>
    public bool Delete(string path, out string? why) => Delete(path, out why, out _);

    /// <summary>Stop the worker: it writes whatever is still pending (an older snapshot than a
    /// synchronous publish is fenced, so this cannot go backwards) and exits. The drain is
    /// load-bearing: a window that closed while others were open only queued its close-time tree,
    /// and this is that snapshot's last chance. Bounded by <paramref name="wait"/>: a worker stuck in
    /// the filesystem past that is left to die with the process, and what it was holding is lost.</summary>
    public void Shutdown(TimeSpan wait)
    {
        _stopping.Set();
        _wake.Set();
        if (_worker is { IsAlive: true } && !_worker.Join(wait))
            _log?.Invoke($"state writer did not finish within {wait.TotalSeconds:0} s of shutdown");
    }

    private void Run()
    {
        while (true)
        {
            _wake.WaitOne();
            // The settle is cut short by a shutdown, never by another Enqueue (that is the burst it exists for).
            bool stopping = _settle <= TimeSpan.Zero ? _stopping.WaitOne(0) : _stopping.WaitOne(_settle);
            Drain();
            if (stopping) return;
        }
    }

    private void Drain()
    {
        List<(string path, Slot slot, string text, long stamp)> work;
        lock (_lock)
        {
            work = _slots.Where(kv => kv.Value.Pending is not null)
                         .Select(kv => (kv.Key, kv.Value, kv.Value.Pending!, kv.Value.PendingStamp)).ToList();
            foreach (var (_, slot, _, _) in work) slot.Pending = null;
        }
        foreach (var (path, slot, text, stamp) in work)
        {
            BeforeWrite?.Invoke(path);
            if (!Write(slot, path, text, stamp, out var why)) _log?.Invoke($"state save to {path} failed: {why}");
        }
        Interlocked.Increment(ref _drains);
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
