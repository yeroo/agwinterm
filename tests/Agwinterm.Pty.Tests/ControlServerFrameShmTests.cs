using System.IO.MemoryMappedFiles;
using System.Text.Json;
using Agwinterm.Core;
using Agwinterm.Pty;

namespace Agwinterm.Pty.Tests;

/// <summary>
/// The <c>image.frameshm</c> control verb, end to end from JSON args through a real
/// <see cref="MemoryMappedFile"/> into the emulator's image table. Two things are being pinned
/// here: that a well-behaved producer's pixels arrive intact and are cached by <c>(id, seq)</c>,
/// and that every way a producer can lie answers <c>{"ok":false,...}</c> with the session
/// untouched — a rejected frame must not half-apply. See docs/specs/image-frameshm.md.
/// </summary>
public class ControlServerFrameShmTests : IDisposable
{
    private const int Width = 8;
    private const int Height = 4;
    private const int Stride = Width * 4;

    private readonly List<IDisposable> _open = [];

    public void Dispose()
    {
        for (int i = _open.Count - 1; i >= 0; i--) _open[i].Dispose();
        _open.Clear();
        GC.SuppressFinalize(this);
    }

    private static (ControlServer server, TerminalSession session) New()
    {
        var session = new TerminalSession(80, 24);
        return (new ControlServer(session), session);
    }

    /// <summary>Padded BGRA whose every pixel carries the frame's tag, so a copy is identifiable.</summary>
    private static byte[] Frame(byte tag, int width = Width, int height = Height, int stride = Stride)
        => ShmTestProducer.Frame(tag, width, height, stride);

    /// <summary>The tightly-packed bytes the reader is expected to hand the emulator.</summary>
    private static byte[] Packed(byte tag, int width = Width, int height = Height)
        => ShmTestProducer.Packed(tag, width, height);

    private ShmTestProducer NewProducer(int slotCount = 2)
    {
        var p = new ShmTestProducer(slotCount);
        _open.Add(p);
        return p;
    }

    private static string Request(string json) => "{\"cmd\":\"image.frameshm\",\"args\":" + json + "}";

    /// <summary>
    /// The decoded <c>error</c> string of a reply. Worth the parse: JsonSerializer escapes an
    /// apostrophe to <c>'</c>, so a substring match against the raw reply silently misses
    /// every message that names a field as 'seq'.
    /// </summary>
    private static string ErrorOf(string resp)
    {
        using var doc = JsonDocument.Parse(resp);
        Assert.False(doc.RootElement.GetProperty("ok").GetBoolean(), resp);
        return doc.RootElement.GetProperty("error").GetString() ?? "";
    }

    private static string Image(
        ShmTestProducer p, long seq, int slot, int id = 1, int row = 0, int col = 0, int cols = 0, int rows = 0)
        => "{\"id\":" + id + ",\"name\":" + JsonSerializer.Serialize(p.Name) +
           ",\"slot\":" + slot + ",\"seq\":" + seq +
           ",\"row\":" + row + ",\"col\":" + col + ",\"cols\":" + cols + ",\"rows\":" + rows + "}";

    // ---- the happy path -----------------------------------------------------------------------

    [Fact]
    public void SingleFrame_ProducesTheExpectedImageAndPlacement()
    {
        var (server, session) = New();
        var p = NewProducer();
        long seq = p.Publish(0x11);

        string resp = server.Dispatch(Request(
            "{\"images\":[" + Image(p, seq, p.Slot, id: 7, row: 2, col: 3, cols: 10, rows: 5) + "]}"));

        Assert.Contains("\"ok\":true", resp);
        Assert.Contains("frame:1/1", resp);

        Assert.True(session.Emulator.Images.ContainsKey(7));
        var img = session.Emulator.Images[7];
        Assert.Equal(KittyFormat.Bgra, img.Format);
        Assert.Equal(Width, img.Width);
        Assert.Equal(Height, img.Height);
        Assert.Equal(Packed(0x11), img.Data);

        var placement = Assert.Single(session.Emulator.Placements);
        Assert.Equal(7, placement.ImageId);
        Assert.Equal(2, placement.Row);
        Assert.Equal(3, placement.Col);
        Assert.Equal(10, placement.Cols);
        Assert.Equal(5, placement.Rows);
    }

    [Fact]
    public void AFrameClearsStalePlacementsJustLikeImageFrame()
    {
        var (server, session) = New();
        var p = NewProducer();

        server.Dispatch(Request("{\"images\":[" + Image(p, p.Publish(0x01), p.Slot, id: 99) + "]}"));
        Assert.Single(session.Emulator.Placements);

        string resp = server.Dispatch(Request(
            "{\"images\":[" + Image(p, p.Publish(0x02), p.Slot, id: 1, row: 1) + "," +
                             Image(p, p.Seq, p.Slot, id: 2, row: 6) + "]}"));

        Assert.Contains("\"ok\":true", resp);
        Assert.Equal(2, session.Emulator.Placements.Count); // id 99 gone, two new ones
        Assert.Equal(1, session.Emulator.Placements[0].ImageId);
        Assert.Equal(2, session.Emulator.Placements[1].ImageId);
    }

    // ---- the (id, seq) re-transmit cache ------------------------------------------------------

    [Fact]
    public void ARepeatedSeqSkipsTheCopyButStillPlaces()
    {
        var (server, session) = New();
        var p = NewProducer();
        long seq = p.Publish(0x33);

        Assert.Contains("frame:1/1", server.Dispatch(Request("{\"images\":[" + Image(p, seq, p.Slot) + "]}")));

        // Same (id, seq): the producer is saying "nothing changed". The copy is skipped, but the
        // placement still happens, so re-placing a cached texture stays free.
        string resp = server.Dispatch(Request("{\"images\":[" + Image(p, seq, p.Slot, row: 4) + "]}"));
        Assert.Contains("\"ok\":true", resp);
        Assert.Contains("frame:1/0", resp);
        Assert.Equal(4, Assert.Single(session.Emulator.Placements).Row);
        Assert.Equal(Packed(0x33), session.Emulator.Images[1].Data);
    }

    [Fact]
    public void ABumpedSeqIsAcceptedAndReplacesThePixels()
    {
        var (server, session) = New();
        var p = NewProducer();

        server.Dispatch(Request("{\"images\":[" + Image(p, p.Publish(0x44), p.Slot) + "]}"));
        Assert.Equal(Packed(0x44), session.Emulator.Images[1].Data);

        string resp = server.Dispatch(Request("{\"images\":[" + Image(p, p.Publish(0x55), p.Slot) + "]}"));
        Assert.Contains("frame:1/1", resp);
        Assert.Equal(Packed(0x55), session.Emulator.Images[1].Data);
    }

    [Fact]
    public void TheCacheIsKeyedByIdSoTwoIdsDoNotShadowEachOther()
    {
        var (server, session) = New();
        var p = NewProducer();
        long seq = p.Publish(0x66);

        // id 1 caches seq. id 2 has never been seen, so the same seq must still transmit.
        string resp = server.Dispatch(Request(
            "{\"images\":[" + Image(p, seq, p.Slot, id: 1) + "," + Image(p, seq, p.Slot, id: 2) + "]}"));
        Assert.Contains("frame:2/2", resp);

        resp = server.Dispatch(Request(
            "{\"images\":[" + Image(p, seq, p.Slot, id: 1) + "," + Image(p, seq, p.Slot, id: 2) + "]}"));
        Assert.Contains("frame:2/0", resp);
        Assert.Equal(2, session.Emulator.Images.Count);
    }

    [Fact]
    public void SeqZeroMeansReadWhateverIsThereAndIsNeverCached()
    {
        var (server, session) = New();
        var p = NewProducer();
        p.Publish(0x77);

        // seq 0 skips the published-frame check and the cache both: the producer is asking for
        // the slot's current contents, and "current" is never something we can memoise.
        for (int i = 0; i < 3; i++)
            Assert.Contains("frame:1/1", server.Dispatch(Request("{\"images\":[" + Image(p, 0, p.Slot) + "]}")));
        Assert.Equal(Packed(0x77), session.Emulator.Images[1].Data);
    }

    // ---- malformed args -----------------------------------------------------------------------

    [Fact]
    public void MissingImagesArrayIsRejected()
    {
        var (server, _) = New();
        Assert.Contains("requires args.images array", ErrorOf(server.Dispatch("{\"cmd\":\"image.frameshm\"}")));
        Assert.Contains("requires args.images array", ErrorOf(server.Dispatch(Request("{}"))));
        Assert.Contains("requires args.images array", ErrorOf(server.Dispatch(Request("{\"images\":42}"))));
    }

    [Fact]
    public void AnImageWithoutANameIsRejected()
    {
        var (server, session) = New();
        string resp = server.Dispatch(Request("{\"images\":[{\"id\":1,\"slot\":0,\"seq\":1}]}"));
        Assert.Contains("requires a string 'name'", ErrorOf(resp));
        Assert.Empty(session.Emulator.Placements);
    }

    [Fact]
    public void AnImageThatIsNotAnObjectIsRejected()
    {
        var (server, _) = New();
        string resp = server.Dispatch(Request("{\"images\":[\"nope\"]}"));
        Assert.Contains("must be an object", ErrorOf(resp));
    }

    [Theory]
    [InlineData("slot")]
    [InlineData("seq")]
    [InlineData("id")]
    [InlineData("width")]
    [InlineData("row")]
    [InlineData("cols")]
    public void AStringWhereANumberBelongsIsRejectedByName(string field)
    {
        var (server, session) = New();
        var p = NewProducer();
        p.Publish(0x88);

        string resp = server.Dispatch(Request(
            "{\"images\":[{\"name\":" + JsonSerializer.Serialize(p.Name) +
            ",\"slot\":0,\"seq\":1,\"" + field + "\":\"1\"}]}"));

        Assert.Contains($"'{field}' must be a JSON number", ErrorOf(resp));
        Assert.Empty(session.Emulator.Placements); // rejected before phase 2 ran
    }

    [Fact]
    public void AnIdThatIsNotAWholeNumberIsRejected()
    {
        var (server, _) = New();
        var p = NewProducer();
        p.Publish(0x99);

        string resp = server.Dispatch(Request(
            "{\"images\":[{\"name\":" + JsonSerializer.Serialize(p.Name) + ",\"slot\":0,\"seq\":1,\"id\":1.5}]}"));
        Assert.Contains("'id' must be a whole number", ErrorOf(resp));
    }

    [Fact]
    public void AnIdOutsideInt32RangeIsRejected()
    {
        var (server, _) = New();
        var p = NewProducer();
        p.Publish(0xAA);

        string resp = server.Dispatch(Request(
            "{\"images\":[{\"name\":" + JsonSerializer.Serialize(p.Name) +
            ",\"slot\":0,\"seq\":1,\"id\":9999999999}]}"));
        Assert.Contains("out of range for a 32-bit integer", ErrorOf(resp));
    }

    // ---- rejections from the reader, surfaced through the verb --------------------------------

    [Fact]
    public void ANameOutsideThePrefixIsRejectedAndLeavesTheSessionUsable()
    {
        var (server, session) = New();
        string resp = server.Dispatch(Request(
            "{\"images\":[{\"id\":1,\"name\":\"Global\\\\anything\",\"slot\":0,\"seq\":1}]}"));

        Assert.Contains(ShmFrameReader.NamePrefix, ErrorOf(resp));
        Assert.Empty(session.Emulator.Placements);
        Assert.Contains("\"ok\":true", server.Dispatch("{\"cmd\":\"ping\"}"));
    }

    [Fact]
    public void AVanishedProducerIsAnOrdinaryFailure()
    {
        var (server, session) = New();
        var p = NewProducer();
        p.Publish(0xBB);
        string name = p.Name;
        long seq = p.Seq;
        int slot = p.Slot;

        p.Dispose();
        _open.Remove(p);

        string resp = server.Dispatch(Request(
            "{\"images\":[{\"id\":1,\"name\":" + JsonSerializer.Serialize(name) +
            ",\"slot\":" + slot + ",\"seq\":" + seq + "}]}"));

        Assert.Contains("producer gone", ErrorOf(resp));
        Assert.Empty(session.Emulator.Placements);
    }

    [Fact]
    public void AGeometryDisagreementIsRejectedAndNothingIsApplied()
    {
        var (server, session) = New();
        var p = NewProducer();
        long seq = p.Publish(0xCC);

        string resp = server.Dispatch(Request(
            "{\"images\":[{\"id\":1,\"name\":" + JsonSerializer.Serialize(p.Name) +
            ",\"slot\":" + p.Slot + ",\"seq\":" + seq + ",\"width\":" + (Width + 1) + "}]}"));

        Assert.Contains("disagrees with the slot descriptor", ErrorOf(resp));
        Assert.Empty(session.Emulator.Placements);
        Assert.Empty(session.Emulator.Images);
    }

    [Fact]
    public void ASecondImageFailingLeavesTheFirstUnapplied()
    {
        var (server, session) = New();
        var p = NewProducer();
        long seq = p.Publish(0xDD);

        // A frame is all-or-nothing: the pane must never be left showing image 1 of a two-image
        // frame, so the good first entry is discarded along with the bad second one.
        string resp = server.Dispatch(Request(
            "{\"images\":[" + Image(p, seq, p.Slot, id: 1) +
            ",{\"id\":2,\"name\":\"Local\\\\nope\",\"slot\":0,\"seq\":1}]}"));

        ErrorOf(resp);
        Assert.Empty(session.Emulator.Placements);
        Assert.Empty(session.Emulator.Images);
    }

    [Fact]
    public void ASeqAheadOfTheProducersReadyIsRejected()
    {
        var (server, _) = New();
        var p = NewProducer();
        long seq = p.Publish(0xEE);

        string resp = server.Dispatch(Request(
            "{\"images\":[" + Image(p, seq + 1, ShmFrameLayout.SlotForSequence(seq + 1, p.SlotCount)) + "]}"));
        Assert.Contains("newer than the mapping's ready sequence", ErrorOf(resp));
    }

    // ---- a producer running ahead of the consumer ---------------------------------------------

    [Fact]
    public void AProducerThatSerialisesOnTheReplyNeverLosesAFrame()
    {
        // The obligation the spec makes normative, and the reason two slots are enough: the
        // producer waits for each reply before filling the next slot, so it can never wrap onto
        // the slot being copied. Every frame arrives whole and in order.
        var (server, session) = New();
        var p = NewProducer(slotCount: 2);

        for (byte tag = 1; tag <= 6; tag++)
        {
            long seq = p.Publish(tag);
            string resp = server.Dispatch(Request("{\"images\":[" + Image(p, seq, p.Slot) + "]}"));
            Assert.Contains("frame:1/1", resp);
            Assert.Equal(Packed(tag), session.Emulator.Images[1].Data);
        }
    }

    [Fact]
    public void AProducerRunningAheadOnTwoSlotsSubstitutesANewerFrameRatherThanTearing()
    {
        // The case two slots do NOT cover. The producer pipelines three frames before draining
        // any reply, so seq 3 has already overwritten the slot seq 1 lived in. What comes back is
        // frame 3's pixels under frame 1's request — a whole, self-consistent frame, just not the
        // one asked for. That is the documented cost of breaking the serialise-on-reply rule, and
        // it is why a pipelining producer must raise slotCount instead.
        var (server, session) = New();
        var p = NewProducer(slotCount: 2);

        p.Publish(0x01);
        int staleSlot = p.Slot;
        long staleSeq = p.Seq;
        p.Publish(0x02);
        p.Publish(0x03);
        Assert.Equal(staleSlot, p.Slot); // seq 3 wrapped back onto seq 1's slot

        string resp = server.Dispatch(Request("{\"images\":[" + Image(p, staleSeq, staleSlot) + "]}"));

        Assert.Contains("\"ok\":true", resp);
        var data = session.Emulator.Images[1].Data;
        Assert.Equal(Packed(0x03), data);       // frame 3, not frame 1
        Assert.NotEqual(Packed(0x01), data);
        // Whole, not torn: no row carries a different frame's tag than any other.
        for (int i = 2; i < data.Length; i += 4) Assert.Equal(0x03, data[i]);
    }

    [Fact]
    public void AProducerRunningAheadWithEnoughSlotsDeliversEveryFrameIntact()
    {
        // The fix the spec prescribes for a pipelining producer: more slots than frames in flight.
        // Three frames are published before any is drained, and each request still gets its own.
        var (server, session) = New();
        var p = NewProducer(slotCount: 4);

        var published = new List<(long seq, int slot, byte tag)>();
        for (byte tag = 1; tag <= 3; tag++)
        {
            long seq = p.Publish(tag);
            published.Add((seq, p.Slot, tag));
        }

        foreach (var (seq, slot, tag) in published)
        {
            string resp = server.Dispatch(Request("{\"images\":[" + Image(p, seq, slot) + "]}"));
            Assert.Contains("frame:1/1", resp);
            Assert.Equal(Packed(tag), session.Emulator.Images[1].Data);
        }
    }
}
