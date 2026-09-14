using Xunit;

namespace Agwinterm.Core.Tests;

/// <summary>
/// The rules StateWriter's summary promises, each against a real temp directory (#294). The fence
/// cases put the worker exactly where the race is — a snapshot taken out of the queue but not yet
/// written (the <c>BeforeWrite</c> seam) — and then publish something newer past it. Each of those
/// fails with the fence line, or the skip's fence line, removed; the earlier shape of these tests
/// did not, because a synchronous publish emptied the queue before the worker ever looked.
/// </summary>
public sealed class StateWriterTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "agwinterm-statewriter-" + Guid.NewGuid().ToString("N"));
    private readonly List<string> _log = new();
    private string P(string name) => Path.Combine(_dir, name);

    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { } }

    private static readonly DateTime Epoch = new(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>Wind the file's clock back so a later write is visible as a changed mtime.</summary>
    private static void MarkOld(string path) => File.SetLastWriteTimeUtc(path, Epoch);
    private static bool Rewritten(string path) => File.GetLastWriteTimeUtc(path) != Epoch;

    /// <summary>Hold the worker between taking a snapshot out of the queue and writing it.</summary>
    private sealed class Gate
    {
        public readonly ManualResetEventSlim Entered = new(false), Release = new(false);
        public void Hold(string _) { Entered.Set(); Release.Wait(); }
    }

    private static void WaitDrains(StateWriter w, long atLeast) =>
        Assert.True(SpinWait.SpinUntil(() => w.Drains >= atLeast, 5000), $"the worker did not drain (drains={w.Drains})");

    [Fact]
    public void Publish_writes_now_and_leaves_no_temp_file()
    {
        var w = new StateWriter(TimeSpan.FromMilliseconds(50), _log.Add);
        Assert.True(w.Publish(P("a.json"), "one", out var why), why);
        Assert.Equal("one", File.ReadAllText(P("a.json")));
        Assert.False(File.Exists(P("a.json.tmp")));
        w.Shutdown(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void Enqueue_coalesces_a_burst_into_the_newest_snapshot()
    {
        var w = new StateWriter(TimeSpan.FromMilliseconds(150), _log.Add);
        w.Enqueue(P("a.json"), "first");
        w.Enqueue(P("a.json"), "second");
        w.Enqueue(P("a.json"), "third");
        Assert.Equal(1, w.PendingCount);             // one slot, replaced not queued
        WaitDrains(w, 1);
        Assert.Equal(0, w.PendingCount);
        Assert.Equal("third", File.ReadAllText(P("a.json")));
        Assert.Empty(_log);
        w.Shutdown(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void Unchanged_bytes_are_not_rewritten_while_the_file_exists()
    {
        var w = new StateWriter(TimeSpan.Zero, _log.Add);
        Assert.True(w.Publish(P("a.json"), "same", out _));
        MarkOld(P("a.json"));
        Assert.True(w.Publish(P("a.json"), "same", out _));
        Assert.False(Rewritten(P("a.json")), "an equal snapshot must cost no write");
        // ...but a file that vanished is written again from the same bytes.
        File.Delete(P("a.json"));
        Assert.True(w.Publish(P("a.json"), "same", out _));
        Assert.Equal("same", File.ReadAllText(P("a.json")));
        w.Shutdown(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void An_in_flight_older_snapshot_never_lands_over_a_newer_synchronous_publish()
    {
        var gate = new Gate();
        var w = new StateWriter(TimeSpan.FromMilliseconds(20), _log.Add) { BeforeWrite = gate.Hold };
        Assert.True(w.Publish(P("a.json"), "X", out _));
        w.Enqueue(P("a.json"), "Y");
        Assert.True(gate.Entered.Wait(5000), "the worker never took Y out of the queue");
        Assert.True(w.Publish(P("a.json"), "Z", out _));   // newer, written while Y is in flight
        gate.Release.Set();
        WaitDrains(w, 1);
        Assert.Equal("Z", File.ReadAllText(P("a.json")));
        w.Shutdown(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void A_skipped_equal_snapshot_still_fences_an_in_flight_older_one()
    {
        var gate = new Gate();
        var w = new StateWriter(TimeSpan.FromMilliseconds(20), _log.Add) { BeforeWrite = gate.Hold };
        Assert.True(w.Publish(P("a.json"), "X", out _));
        w.Enqueue(P("a.json"), "Y");                 // the tree went to Y...
        Assert.True(gate.Entered.Wait(5000), "the worker never took Y out of the queue");
        MarkOld(P("a.json"));
        Assert.True(w.Publish(P("a.json"), "X", out _));   // ...and back to X: equal bytes, skipped
        Assert.False(Rewritten(P("a.json")), "the skip must not write");
        gate.Release.Set();                          // Y, older, now reaches the fence
        WaitDrains(w, 1);
        Assert.Equal("X", File.ReadAllText(P("a.json")));
        w.Shutdown(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void A_reserved_stamp_orders_a_late_publish_where_it_was_built()
    {
        var w = new StateWriter(TimeSpan.FromMilliseconds(20), _log.Add);
        // Built first, written last: the queued snapshot is newer and must win.
        long early = w.Reserve();
        w.Enqueue(P("a.json"), "newer");
        Assert.True(w.Publish(P("a.json"), "older", early, out _));
        Assert.Equal(1, w.PendingCount);             // the newer snapshot stays queued
        WaitDrains(w, 1);
        Assert.Equal("newer", File.ReadAllText(P("a.json")));
        // Built last: the queued snapshot is older and is dropped, not written after.
        w.Enqueue(P("a.json"), "stale");
        long late = w.Reserve();
        Assert.True(w.Publish(P("a.json"), "final", late, out _));
        Assert.Equal(0, w.PendingCount);
        WaitDrains(w, 2);
        Assert.Equal("final", File.ReadAllText(P("a.json")));
        w.Shutdown(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void Delete_is_fenced_against_an_in_flight_snapshot_and_forgets_the_bytes()
    {
        var gate = new Gate();
        var w = new StateWriter(TimeSpan.FromMilliseconds(20), _log.Add) { BeforeWrite = gate.Hold };
        Assert.True(w.Publish(P("a.json"), "X", out _));
        w.Enqueue(P("a.json"), "Y");
        Assert.True(gate.Entered.Wait(5000));
        Assert.True(w.Delete(P("a.json"), out var why), why);
        Assert.False(File.Exists(P("a.json")));
        gate.Release.Set();                          // Y is older than the delete
        WaitDrains(w, 1);
        Assert.False(File.Exists(P("a.json")), "a snapshot in flight resurrected a deleted file");
        Assert.True(w.Publish(P("a.json"), "X", out _));   // the same bytes as before the delete: a real write
        Assert.Equal("X", File.ReadAllText(P("a.json")));
        w.Shutdown(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void Shutdown_drains_what_is_still_queued()
    {
        var w = new StateWriter(TimeSpan.FromSeconds(30), _log.Add);   // a settle no test waits for: shutdown cuts it short
        w.Enqueue(P("a.json"), "late");
        w.Shutdown(TimeSpan.FromSeconds(5));
        Assert.Equal("late", File.ReadAllText(P("a.json")));
    }

    [Fact]
    public void A_failed_write_says_why_and_forgets_the_bytes()
    {
        var w = new StateWriter(TimeSpan.Zero, _log.Add);
        Assert.True(w.Publish(P("a.json"), "X", out _));
        Directory.CreateDirectory(P("a.json.tmp"));  // the next write cannot create its temp file
        Assert.False(w.Publish(P("a.json"), "Y", out var why));
        Assert.False(string.IsNullOrEmpty(why));
        File.WriteAllText(P("a.json"), "other");     // meanwhile the file changed under the writer
        Directory.Delete(P("a.json.tmp"));
        // Had the failure kept "X" as the published bytes, this equal snapshot would be skipped and
        // the file left holding "other".
        Assert.True(w.Publish(P("a.json"), "X", out _));
        Assert.Equal("X", File.ReadAllText(P("a.json")));
        w.Shutdown(TimeSpan.FromSeconds(5));
    }
}
