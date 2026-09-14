using Agwinterm.Core;

namespace Agwinterm.Pty.Tests;

/// <summary>
/// The rules StateWriter's summary promises, each against a real temp directory (#294). The two
/// fence cases are the ones that matter: a snapshot still queued when a newer one is published
/// synchronously must never land on top of it — whether that newer one was written, or skipped
/// because the file already held its bytes (the skip that did not move the fence was the Major
/// revmux found in lite's first cut).
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
        Assert.True(SpinWait.SpinUntil(() => File.Exists(P("a.json")), 5000));
        Assert.True(SpinWait.SpinUntil(() => w.PendingCount == 0, 5000));
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
    public void A_queued_older_snapshot_never_lands_over_a_newer_synchronous_publish()
    {
        var w = new StateWriter(TimeSpan.FromMilliseconds(300), _log.Add);
        Assert.True(w.Publish(P("a.json"), "X", out _));
        w.Enqueue(P("a.json"), "Y");                 // waits out the settle
        Assert.True(w.Publish(P("a.json"), "Z", out _));   // newer, written now
        Thread.Sleep(800);                           // let the worker drain Y
        Assert.Equal(0, w.PendingCount);
        Assert.Equal("Z", File.ReadAllText(P("a.json")));
        w.Shutdown(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void A_skipped_equal_snapshot_still_fences_an_older_queued_one()
    {
        var w = new StateWriter(TimeSpan.FromMilliseconds(300), _log.Add);
        Assert.True(w.Publish(P("a.json"), "X", out _));
        w.Enqueue(P("a.json"), "Y");                 // the tree went to Y...
        MarkOld(P("a.json"));
        Assert.True(w.Publish(P("a.json"), "X", out _));   // ...and back to X: equal bytes, skipped
        Assert.False(Rewritten(P("a.json")), "the skip must not write");
        Thread.Sleep(800);                           // the worker now drains Y — an older snapshot
        Assert.Equal(0, w.PendingCount);
        Assert.Equal("X", File.ReadAllText(P("a.json")));
        w.Shutdown(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void Forget_makes_the_next_equal_snapshot_a_real_write()
    {
        var w = new StateWriter(TimeSpan.Zero, _log.Add);
        Assert.True(w.Publish(P("a.json"), "same", out _));
        MarkOld(P("a.json"));
        w.Forget(P("a.json"));
        Assert.True(w.Publish(P("a.json"), "same", out _));
        Assert.True(Rewritten(P("a.json")), "after Forget the same bytes must be written");
        w.Shutdown(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void Shutdown_drains_what_is_still_queued()
    {
        var w = new StateWriter(TimeSpan.FromSeconds(30), _log.Add);   // a settle no test waits for
        w.Enqueue(P("a.json"), "late");
        w.Shutdown(TimeSpan.FromSeconds(5));
        Assert.Equal("late", File.ReadAllText(P("a.json")));
    }

    [Fact]
    public void A_failed_synchronous_write_says_why_and_forgets_the_bytes()
    {
        var w = new StateWriter(TimeSpan.Zero, _log.Add);
        string blocked = P("dir.json");
        Directory.CreateDirectory(blocked);          // a directory where the file should be: the move fails
        Assert.False(w.Publish(blocked, "x", out var why));
        Assert.False(string.IsNullOrEmpty(why));
        w.Shutdown(TimeSpan.FromSeconds(5));
    }
}
