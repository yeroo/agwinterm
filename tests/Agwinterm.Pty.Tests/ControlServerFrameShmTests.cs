using System.IO.MemoryMappedFiles;
using System.Text.Json;
using Agwinterm.Core;
using Agwinterm.Pty;

namespace Agwinterm.Pty.Tests;

/// <summary>
/// The <c>image.frameshm</c> control verb, end to end from JSON args through a real
/// <see cref="MemoryMappedFile"/> into the emulator's image table. Two things are being pinned
/// here: that a well-behaved producer's pixels arrive intact and are cached by
/// <c>(id, mapping name, seq)</c>,
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

    private static (ControlServer server, TerminalSession session) New(long? sharedFrameRequestByteLimit = null)
    {
        var session = new TerminalSession(80, 24);
        var server = sharedFrameRequestByteLimit is { } limit
            ? new ControlServer(session, limit)
            : new ControlServer(session);
        return (server, session);
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

    private string NewRawMapping(ShmFrameHeader header, long capacity = 4096)
    {
        string name = ShmFrameReader.NamePrefix + "acceptance-" + Guid.NewGuid().ToString("N");
        var mmf = MemoryMappedFile.CreateNew(name, capacity);
        _open.Add(mmf);
        using var view = mmf.CreateViewAccessor(0, capacity, MemoryMappedFileAccess.ReadWrite);
        var bytes = new byte[ShmFrameLayout.HeaderSize];
        ShmFrameLayout.Write(bytes, header);
        view.WriteArray(0, bytes, 0, bytes.Length);
        return name;
    }

    private static ShmFrameHeader RawHeader(
        long ready = 1,
        long slotStride = Stride * Height,
        int width = Width,
        int height = Height,
        int stride = Stride,
        int format = (int)KittyFormat.Bgra) => new(
            ShmFrameLayout.Version, SlotCount: 2, Flags: 0, slotStride,
            ShmFrameLayout.HeaderSize, ready,
            [
                new ShmSlotDescriptor(width, height, stride, format),
                new ShmSlotDescriptor(width, height, stride, format),
            ]);

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
    public void FullDocumentedRequestEnvelopePlacesImagesInsideArgs()
    {
        var (server, session) = New();
        var p = NewProducer();
        long seq = p.Publish(0x12);
        string request = "{\"cmd\":\"image.frameshm\",\"target\":\"active\",\"args\":{\"images\":[{" +
            "\"id\":1,\"name\":" + JsonSerializer.Serialize(p.Name) +
            ",\"slot\":" + p.Slot + ",\"seq\":" + seq +
            ",\"width\":" + Width + ",\"height\":" + Height + ",\"stride\":" + Stride +
            ",\"format\":" + (int)KittyFormat.Bgra +
            ",\"row\":0,\"col\":0,\"cols\":8,\"rows\":4}]}}";

        Assert.Contains("frame:1/1", server.Dispatch(request));
        Assert.Equal(Packed(0x12), session.Emulator.Images[1].Data);
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

    // ---- the (id, mapping name, seq) re-transmit cache ----------------------------------------

    [Fact]
    public void ARepeatedSeqSkipsTheCopyButStillPlaces()
    {
        var (server, session) = New();
        var p = NewProducer();
        long seq = p.Publish(0x33);

        Assert.Contains("frame:1/1", server.Dispatch(Request("{\"images\":[" + Image(p, seq, p.Slot) + "]}")));

        // Same (id, name, seq): the producer is saying "nothing changed". The mapping/header is
        // validated and the pixel copy is skipped, but the placement still happens.
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
    public void TheCacheIsKeyedByMappingNameSoAnotherProducerCannotReuseIt()
    {
        var (server, session) = New();
        var first = NewProducer();
        var second = NewProducer();
        long firstSeq = first.Publish(0x67);
        long secondSeq = second.Publish(0x68);
        Assert.Equal(firstSeq, secondSeq);

        Assert.Contains("frame:1/1", server.Dispatch(Request(
            "{\"images\":[" + Image(first, firstSeq, first.Slot) + "]}")));
        string resp = server.Dispatch(Request(
            "{\"images\":[" + Image(second, secondSeq, second.Slot) + "]}"));

        Assert.Contains("frame:1/1", resp);
        Assert.Equal(Packed(0x68), session.Emulator.Images[1].Data);
    }

    [Fact]
    public void ACacheHitStillValidatesThatTheMappingIsAlive()
    {
        var (server, session) = New();
        var p = NewProducer();
        long seq = p.Publish(0x69);
        string request = Request("{\"images\":[" + Image(p, seq, p.Slot) + "]}");
        Assert.Contains("frame:1/1", server.Dispatch(request));

        p.Dispose();
        _open.Remove(p);

        Assert.Contains("producer gone", ErrorOf(server.Dispatch(request)));
        Assert.Equal(Packed(0x69), session.Emulator.Images[1].Data);
        Assert.Single(session.Emulator.Placements);
    }

    [Fact]
    public void ACacheHitStillValidatesRequestGeometry()
    {
        var (server, session) = New();
        var p = NewProducer();
        long seq = p.Publish(0x6A);
        Assert.Contains("frame:1/1", server.Dispatch(Request(
            "{\"images\":[" + Image(p, seq, p.Slot) + "]}")));

        string resp = server.Dispatch(Request(
            "{\"images\":[" + Image(p, seq, p.Slot).TrimEnd('}') +
            ",\"width\":" + (Width + 1) + "}]}"));

        Assert.Contains("disagrees with the slot descriptor", ErrorOf(resp));
        Assert.Equal(Packed(0x6A), session.Emulator.Images[1].Data);
        Assert.Single(session.Emulator.Placements);
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

    [Fact]
    public void TooManyImagesAreRejectedBeforeAnyMappingIsRead()
    {
        var (server, session) = New();
        var p = NewProducer();
        long seq = p.Publish(0xAB);
        string entries = string.Join(",", Enumerable.Repeat(
            Image(p, seq, p.Slot), ControlServer.MaxSharedFrameImages + 1));

        string resp = server.Dispatch(Request("{\"images\":[" + entries + "]}"));

        Assert.Contains($"at most {ControlServer.MaxSharedFrameImages}", ErrorOf(resp));
        Assert.Empty(session.Emulator.Images);
        Assert.Empty(session.Emulator.Placements);
    }

    [Fact]
    public void AggregateCopyBudgetRejectsRepeatedEntriesWithoutHalfApplying()
    {
        long oneFrame = Width * Height * 4L;
        var (server, session) = New(sharedFrameRequestByteLimit: oneFrame);
        var p = NewProducer();
        p.Publish(0xAC);

        string resp = server.Dispatch(Request(
            "{\"images\":[" + Image(p, 0, p.Slot, id: 1) + "," +
                               Image(p, 0, p.Slot, id: 2) + "]}"));

        Assert.Contains("request byte budget", ErrorOf(resp));
        Assert.Empty(session.Emulator.Images);
        Assert.Empty(session.Emulator.Placements);
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
    public void RetryingAnAllOrNothingFrameRetransmitsEntriesStagedBeforeTheFailure()
    {
        var (server, session) = New();
        var p = NewProducer();
        long seq = p.Publish(0xDE);

        string rejected = server.Dispatch(Request(
            "{\"images\":[" + Image(p, seq, p.Slot, id: 1) +
            ",{\"id\":2,\"name\":\"Local\\\\nope\",\"slot\":0,\"seq\":1}]}"));
        ErrorOf(rejected);

        string retried = server.Dispatch(Request(
            "{\"images\":[" + Image(p, seq, p.Slot, id: 1) + "," +
                               Image(p, seq, p.Slot, id: 2) + "]}"));

        Assert.Contains("frame:2/2", retried);
        Assert.Equal(new[] { 1, 2 }, session.Emulator.Images.Keys.Order().ToArray());
        Assert.Equal(2, session.Emulator.Placements.Count);
    }

    [Fact]
    public void SwitchingBetweenFileAndSharedMemoryFramesInvalidatesTheOtherTransportCache()
    {
        var (server, session) = New();
        var p = NewProducer();
        long seq = p.Publish(0xDF);
        string file = Path.GetTempFileName();
        byte[] fileBytes = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A };
        File.WriteAllBytes(file, fileBytes);
        try
        {
            string fileRequest = "{\"cmd\":\"image.frame\",\"args\":{\"images\":[{\"id\":1,\"path\":" +
                                 JsonSerializer.Serialize(file) + "}]}}";
            string shmRequest = Request("{\"images\":[" + Image(p, seq, p.Slot, id: 1) + "]}");

            Assert.Contains("frame:1/1", server.Dispatch(fileRequest));
            Assert.Equal(fileBytes, session.Emulator.Images[1].Data);
            Assert.Contains("frame:1/1", server.Dispatch(shmRequest));
            Assert.Equal(Packed(0xDF), session.Emulator.Images[1].Data);

            Assert.Contains("frame:1/1", server.Dispatch(fileRequest));
            Assert.Equal(fileBytes, session.Emulator.Images[1].Data);
            Assert.Contains("frame:1/1", server.Dispatch(shmRequest));
            Assert.Equal(Packed(0xDF), session.Emulator.Images[1].Data);
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public void ACacheHitRetransmitsAfterTheImageIdWasReplacedOutsideTheFrameVerb()
    {
        var (server, session) = New();
        var p = NewProducer();
        long seq = p.Publish(0xE0);
        string request = Request("{\"images\":[" + Image(p, seq, p.Slot, id: 1) + "]}");
        Assert.Contains("frame:1/1", server.Dispatch(request));

        session.MutateLocked(em => em.SetImageData(1, KittyFormat.Rgba, 1, 1, new byte[] { 1, 2, 3, 4 }));

        Assert.Contains("frame:1/1", server.Dispatch(request));
        Assert.Equal(Packed(0xE0), session.Emulator.Images[1].Data);
    }

    // Two cached ids, one replaced behind the verb's back: the retry copies that one out of shared
    // memory and the live sibling keeps its cache hit - the burst this cache exists to avoid.
    [Fact]
    public void OnlyTheStaleIdIsRecopiedWhenASiblingIsStillLive()
    {
        var (server, session) = New();
        var a = NewProducer();
        var b = NewProducer();
        long seqA = a.Publish(0xA1);
        long seqB = b.Publish(0xB1);
        string request = Request("{\"images\":[" + Image(a, seqA, a.Slot, id: 1) + "," +
            Image(b, seqB, b.Slot, id: 2, row: 6) + "]}");
        Assert.Contains("frame:2/2", server.Dispatch(request));
        Assert.Contains("frame:2/0", server.Dispatch(request));

        session.MutateLocked(em => em.SetImageData(2, KittyFormat.Rgba, 1, 1, new byte[] { 1, 2, 3, 4 }));

        Assert.Contains("frame:2/1", server.Dispatch(request));
        Assert.Equal(Packed(0xA1), session.Emulator.Images[1].Data);
        Assert.Equal(Packed(0xB1), session.Emulator.Images[2].Data);
        Assert.Equal(2, session.Emulator.Placements.Count);
    }

    [Fact]
    public void ACacheReplacementBetweenValidationAndCommitRetransmitsOnce()
    {
        // Phase 1 answers a cache hit from the dictionary alone, so the emulator can lose the
        // cached image any time before phase 2 takes the lock. Phase 2 must notice, drop the
        // entry and retry once with the pixels re-read - the caller sees one clean success.
        using var session = new TerminalSession(80, 24);
        var server = new ControlServer(session);
        var p = NewProducer();
        long seq = p.Publish(0xE1);
        string request = Request("{\"images\":[" + Image(p, seq, p.Slot, id: 1) + "]}");
        Assert.Contains("frame:1/1", server.Dispatch(request));

        int preparedCalls = 0;
        server.SharedFramePrepared = () =>
        {
            if (Interlocked.Increment(ref preparedCalls) == 1)
                session.MutateLocked(em =>
                    em.SetImageData(1, KittyFormat.Rgba, 1, 1, new byte[] { 1, 2, 3, 4 }));
        };

        string resp = server.Dispatch(request);
        Assert.Equal(2, preparedCalls);

        Assert.Contains("frame:1/1", resp);
        Assert.Equal(Packed(0xE1), session.Emulator.Images[1].Data);
        Assert.Single(session.Emulator.Placements);
    }

    [Fact]
    public async Task ASecondCacheReplacementDuringTheRetryReturnsAnErrorWithoutChangingPlacements()
    {
        using var session = new TerminalSession(80, 24);
        var server = new ControlServer(session);
        var p = NewProducer();
        long seq = p.Publish(0xE2);
        string request = Request("{\"images\":[" + Image(p, seq, p.Slot, id: 1, row: 3) + "]}");
        Assert.Contains("frame:1/1", server.Dispatch(request));

        using var firstValidationDone = new ManualResetEventSlim();
        using var resumeFirstRequest = new ManualResetEventSlim();
        byte[] finalExternalPixels = { 9, 8, 7, 6 };
        // Prepared-seam order: 1 = the paused first request, 2 and 3 = the test thread's request
        // and its retry, 4 = the first request's retry, which finds the re-cached entry and then
        // loses it once more before it can commit.
        int preparedCalls = 0;
        server.SharedFramePrepared = () =>
        {
            switch (Interlocked.Increment(ref preparedCalls))
            {
                case 1:
                    firstValidationDone.Set();
                    if (!resumeFirstRequest.Wait(TimeSpan.FromSeconds(10)))
                        throw new TimeoutException("test did not release the first cache validation");
                    break;
                case 4:
                    session.MutateLocked(em =>
                        em.SetImageData(1, KittyFormat.Rgba, 1, 1, finalExternalPixels));
                    break;
            }
        };

        Task<string> firstRequest = Task.Run(() => server.Dispatch(request));
        try
        {
            Assert.True(firstValidationDone.Wait(TimeSpan.FromSeconds(10)),
                "first request did not reach cache validation");
            session.MutateLocked(em =>
                em.SetImageData(1, KittyFormat.Rgba, 1, 1, new byte[] { 5, 6, 7, 8 }));

            // Re-cache the same sequence with a new image while the first request is paused. Its
            // retry will accept this entry in phase 1, then the seam hook replaces it once more.
            Assert.Contains("frame:1/1", server.Dispatch(request));
            Assert.Equal(3, preparedCalls);
        }
        finally
        {
            resumeFirstRequest.Set();
        }

        string resp = await firstRequest.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Contains("cache changed while the frame was prepared", ErrorOf(resp));
        Assert.Equal(finalExternalPixels, session.Emulator.Images[1].Data);
        var placement = Assert.Single(session.Emulator.Placements);
        Assert.Equal(3, placement.Row);
    }

    [Fact]
    public async Task AnOlderConcurrentSequenceCannotReplaceANewerCommittedFrame()
    {
        using var session = new TerminalSession(80, 24);
        var server = new ControlServer(session);
        var p = NewProducer(slotCount: 2);
        long olderSeq = p.Publish(0xA1);
        int olderSlot = p.Slot;
        long newerSeq = p.Publish(0xA2);
        int newerSlot = p.Slot;

        using var olderPrepared = new ManualResetEventSlim();
        using var releaseOlder = new ManualResetEventSlim();
        int preparedCalls = 0;
        server.SharedFramePrepared = () =>
        {
            if (Interlocked.Increment(ref preparedCalls) != 1) return;
            olderPrepared.Set();
            if (!releaseOlder.Wait(TimeSpan.FromSeconds(10)))
                throw new TimeoutException("test did not release the older prepared frame");
        };

        Task<string> older = Task.Run(() => server.Dispatch(Request(
            "{\"images\":[" + Image(p, olderSeq, olderSlot, id: 1) + "]}")));
        string newerResponse;
        try
        {
            Assert.True(olderPrepared.Wait(TimeSpan.FromSeconds(10)),
                "older request did not finish phase 1");
            newerResponse = server.Dispatch(Request(
                "{\"images\":[" + Image(p, newerSeq, newerSlot, id: 1) + "]}"));
        }
        finally
        {
            releaseOlder.Set();
        }

        string olderResponse = await older.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Contains("frame:1/1", newerResponse);
        Assert.Contains("was superseded", ErrorOf(olderResponse));
        Assert.Equal(Packed(0xA2), session.Emulator.Images[1].Data);
        Assert.Single(session.Emulator.Placements);
    }

    [Fact]
    public async Task ADelayedSeqZeroRequestCannotReplaceANewerPositiveSequenceFromTheSameMapping()
    {
        using var session = new TerminalSession(80, 24);
        var server = new ControlServer(session);
        var p = NewProducer(slotCount: 2);
        p.Publish(0xA3);
        int olderSlot = p.Slot;
        long newerSeq = p.Publish(0xA4);
        int newerSlot = p.Slot;

        using var olderPrepared = new ManualResetEventSlim();
        using var releaseOlder = new ManualResetEventSlim();
        int preparedCalls = 0;
        server.SharedFramePrepared = () =>
        {
            if (Interlocked.Increment(ref preparedCalls) != 1) return;
            olderPrepared.Set();
            if (!releaseOlder.Wait(TimeSpan.FromSeconds(10)))
                throw new TimeoutException("test did not release the seq-zero prepared frame");
        };

        Task<string> older = Task.Run(() => server.Dispatch(Request(
            "{\"images\":[" + Image(p, 0, olderSlot, id: 1) + "]}")));
        string newerResponse;
        try
        {
            Assert.True(olderPrepared.Wait(TimeSpan.FromSeconds(10)),
                "seq-zero request did not finish phase 1");
            newerResponse = server.Dispatch(Request(
                "{\"images\":[" + Image(p, newerSeq, newerSlot, id: 1) + "]}"));
        }
        finally
        {
            releaseOlder.Set();
        }

        string olderResponse = await older.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Contains("frame:1/1", newerResponse);
        Assert.Contains("was superseded", ErrorOf(olderResponse));
        Assert.Equal(Packed(0xA4), session.Emulator.Images[1].Data);
    }

    [Fact]
    public async Task ADelayedPositiveSequenceCannotReplaceANewerSeqZeroRequestFromTheSameMapping()
    {
        using var session = new TerminalSession(80, 24);
        var server = new ControlServer(session);
        var p = NewProducer(slotCount: 2);
        long olderSeq = p.Publish(0xA5);
        int olderSlot = p.Slot;
        p.Publish(0xA6);
        int newerSlot = p.Slot;

        using var olderPrepared = new ManualResetEventSlim();
        using var releaseOlder = new ManualResetEventSlim();
        int preparedCalls = 0;
        server.SharedFramePrepared = () =>
        {
            if (Interlocked.Increment(ref preparedCalls) != 1) return;
            olderPrepared.Set();
            if (!releaseOlder.Wait(TimeSpan.FromSeconds(10)))
                throw new TimeoutException("test did not release the positive-sequence prepared frame");
        };

        Task<string> older = Task.Run(() => server.Dispatch(Request(
            "{\"images\":[" + Image(p, olderSeq, olderSlot, id: 1) + "]}")));
        string newerResponse;
        try
        {
            Assert.True(olderPrepared.Wait(TimeSpan.FromSeconds(10)),
                "positive-sequence request did not finish phase 1");
            newerResponse = server.Dispatch(Request(
                "{\"images\":[" + Image(p, 0, newerSlot, id: 1) + "]}"));
        }
        finally
        {
            releaseOlder.Set();
        }

        string olderResponse = await older.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Contains("frame:1/1", newerResponse);
        Assert.Contains("was superseded", ErrorOf(olderResponse));
        Assert.Equal(Packed(0xA6), session.Emulator.Images[1].Data);
    }

    [Fact]
    public async Task AcceptedSequenceOrderingSurvivesPixelCacheInvalidation()
    {
        using var session = new TerminalSession(80, 24);
        var server = new ControlServer(session);
        var p = NewProducer(slotCount: 2);
        long olderSeq = p.Publish(0xB1);
        int olderSlot = p.Slot;
        long newerSeq = p.Publish(0xB2);
        int newerSlot = p.Slot;
        string newerRequest = Request(
            "{\"images\":[" + Image(p, newerSeq, newerSlot, id: 1) + "]}");
        Assert.Contains("frame:1/1", server.Dispatch(newerRequest));

        // Replacing the image invalidates and removes the retransmit-cache entry during the next
        // phase 1. Accepted sequence order is separate state and must remain authoritative.
        session.MutateLocked(em =>
            em.SetImageData(1, KittyFormat.Rgba, 1, 1, new byte[] { 1, 2, 3, 4 }));
        using var newerPrepared = new ManualResetEventSlim();
        using var releaseNewer = new ManualResetEventSlim();
        int preparedCalls = 0;
        server.SharedFramePrepared = () =>
        {
            if (Interlocked.Increment(ref preparedCalls) != 1) return;
            newerPrepared.Set();
            if (!releaseNewer.Wait(TimeSpan.FromSeconds(10)))
                throw new TimeoutException("test did not release the cache-invalidated frame");
        };

        Task<string> retransmit = Task.Run(() => server.Dispatch(newerRequest));
        string olderResponse;
        try
        {
            Assert.True(newerPrepared.Wait(TimeSpan.FromSeconds(10)),
                "newer request did not finish cache invalidation and phase 1");
            olderResponse = server.Dispatch(Request(
                "{\"images\":[" + Image(p, olderSeq, olderSlot, id: 1) + "]}"));
        }
        finally
        {
            releaseNewer.Set();
        }

        Assert.Contains("was superseded", ErrorOf(olderResponse));
        Assert.Contains("frame:1/1", await retransmit.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(Packed(0xB2), session.Emulator.Images[1].Data);
    }

    [Fact]
    public async Task AnOlderMappingRequestCannotResurfaceAfterAnotherMappingCommits()
    {
        using var session = new TerminalSession(80, 24);
        var server = new ControlServer(session);
        var producerA = NewProducer(slotCount: 2);
        var producerB = NewProducer(slotCount: 2);
        long olderA = producerA.Publish(0xC1);
        int olderASlot = producerA.Slot;
        long newerA = producerA.Publish(0xC2);
        int newerASlot = producerA.Slot;
        long sequenceB = producerB.Publish(0xC3);

        Assert.Contains("frame:1/1", server.Dispatch(Request(
            "{\"images\":[" + Image(producerA, newerA, newerASlot, id: 1) + "]}")));

        using var olderPrepared = new ManualResetEventSlim();
        using var releaseOlder = new ManualResetEventSlim();
        int preparedCalls = 0;
        server.SharedFramePrepared = () =>
        {
            if (Interlocked.Increment(ref preparedCalls) != 1) return;
            olderPrepared.Set();
            if (!releaseOlder.Wait(TimeSpan.FromSeconds(10)))
                throw new TimeoutException("test did not release the old mapping request");
        };

        Task<string> delayedA = Task.Run(() => server.Dispatch(Request(
            "{\"images\":[" + Image(producerA, olderA, olderASlot, id: 1) + "]}")));
        string responseB;
        try
        {
            Assert.True(olderPrepared.Wait(TimeSpan.FromSeconds(10)),
                "old mapping request did not finish phase 1");
            responseB = server.Dispatch(Request(
                "{\"images\":[" + Image(producerB, sequenceB, producerB.Slot, id: 1) + "]}"));
        }
        finally
        {
            releaseOlder.Set();
        }

        string responseA = await delayedA.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Contains("frame:1/1", responseB);
        Assert.Contains("was superseded", ErrorOf(responseA));
        Assert.Equal(Packed(0xC3), session.Emulator.Images[1].Data);
    }

    [Fact]
    public async Task AnOlderMappingRequestCannotResurfaceAfterASeqZeroMappingCommits()
    {
        using var session = new TerminalSession(80, 24);
        var server = new ControlServer(session);
        var producerA = NewProducer(slotCount: 2);
        var producerB = NewProducer(slotCount: 2);
        long sequenceA = producerA.Publish(0xC4);
        producerB.Publish(0xC5);

        using var olderPrepared = new ManualResetEventSlim();
        using var releaseOlder = new ManualResetEventSlim();
        int preparedCalls = 0;
        server.SharedFramePrepared = () =>
        {
            if (Interlocked.Increment(ref preparedCalls) != 1) return;
            olderPrepared.Set();
            if (!releaseOlder.Wait(TimeSpan.FromSeconds(10)))
                throw new TimeoutException("test did not release the old mapping request");
        };

        Task<string> delayedA = Task.Run(() => server.Dispatch(Request(
            "{\"images\":[" + Image(producerA, sequenceA, producerA.Slot, id: 1) + "]}")));
        string responseB;
        try
        {
            Assert.True(olderPrepared.Wait(TimeSpan.FromSeconds(10)),
                "old mapping request did not finish phase 1");
            responseB = server.Dispatch(Request(
                "{\"images\":[" + Image(producerB, 0, producerB.Slot, id: 1) + "]}"));
        }
        finally
        {
            releaseOlder.Set();
        }

        string responseA = await delayedA.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Contains("frame:1/1", responseB);
        Assert.Contains("was superseded", ErrorOf(responseA));
        Assert.Equal(Packed(0xC5), session.Emulator.Images[1].Data);
    }

    [Fact]
    public void SequentialUniqueIdsCannotExceedTheRetainedImageLimit()
    {
        using var session = new TerminalSession(80, 24);
        var server = new ControlServer(
            session,
            ControlServer.MaxSharedFrameRequestBytes,
            ControlServer.MaxRetainedSharedFrameBytes,
            retainedSharedFrameImageLimit: 2);
        var p = NewProducer();
        long seq = p.Publish(0xA3);

        Assert.Contains("frame:1/1", server.Dispatch(Request(
            "{\"images\":[" + Image(p, seq, p.Slot, id: 1) + "]}")));
        Assert.Contains("frame:1/1", server.Dispatch(Request(
            "{\"images\":[" + Image(p, seq, p.Slot, id: 2) + "]}")));

        string rejected = server.Dispatch(Request(
            "{\"images\":[" + Image(p, seq, p.Slot, id: 3) + "]}"));
        Assert.Contains("retained image limit", ErrorOf(rejected));
        Assert.Equal(2, session.Emulator.Images.Count);
        Assert.Equal(2, Assert.Single(session.Emulator.Placements).ImageId);
    }

    [Fact]
    public void SequentialUniqueIdsCannotExceedTheRetainedPixelBudget()
    {
        using var session = new TerminalSession(80, 24);
        long oneFrameBytes = Packed(0).LongLength;
        var server = new ControlServer(
            session,
            ControlServer.MaxSharedFrameRequestBytes,
            retainedSharedFrameByteLimit: oneFrameBytes,
            ControlServer.MaxRetainedSharedFrameImages);
        var p = NewProducer();
        long seq = p.Publish(0xA4);

        Assert.Contains("frame:1/1", server.Dispatch(Request(
            "{\"images\":[" + Image(p, seq, p.Slot, id: 1) + "]}")));
        string rejected = server.Dispatch(Request(
            "{\"images\":[" + Image(p, seq, p.Slot, id: 2) + "]}"));

        Assert.Contains("retained pixel budget", ErrorOf(rejected));
        Assert.Single(session.Emulator.Images);
        Assert.Equal(1, Assert.Single(session.Emulator.Placements).ImageId);
    }

    [Fact]
    public void ReplacingAnExistingIdIsAcceptedWhileAnotherPathHoldsTheSessionOverBudget()
    {
        // The budget is a ceiling on what shared frames may ADD, not a gate on the session's total:
        // a Kitty or sixel image that arrived through the pty can put the pane over the limit on
        // its own, and a producer replacing its own id then makes nothing worse, so it must not be
        // locked out. Only a request that would grow the excess is refused.
        using var session = new TerminalSession(80, 24);
        long oneFrameBytes = Packed(0).LongLength;
        var server = new ControlServer(
            session,
            ControlServer.MaxSharedFrameRequestBytes,
            retainedSharedFrameByteLimit: oneFrameBytes,
            ControlServer.MaxRetainedSharedFrameImages);
        var p = NewProducer();

        Assert.Contains("frame:1/1", server.Dispatch(Request(
            "{\"images\":[" + Image(p, p.Publish(0xB1), p.Slot, id: 1) + "]}")));
        // Something else (the pty stream, say) parks four frames' worth of pixels under another id.
        session.MutateLocked(em => em.SetImageData(2, KittyFormat.Png, 0, 0, new byte[oneFrameBytes * 4]));

        string replaced = server.Dispatch(Request(
            "{\"images\":[" + Image(p, p.Publish(0xB2), p.Slot, id: 1) + "]}"));
        Assert.Contains("frame:1/1", replaced);
        Assert.Equal(Packed(0xB2), session.Emulator.Images[1].Data);
        Assert.Equal(1, Assert.Single(session.Emulator.Placements).ImageId);

        string grown = server.Dispatch(Request(
            "{\"images\":[" + Image(p, p.Publish(0xB3), p.Slot, id: 3) + "]}"));
        Assert.Contains("retained pixel budget", ErrorOf(grown));
        Assert.Equal(2, session.Emulator.Images.Count);
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

    [Fact]
    public void APositiveSeqNamingAnotherValidSlotIsRejected()
    {
        var (server, session) = New();
        var p = NewProducer(slotCount: 3);
        long seq = p.Publish(0xEF);
        int wrongSlot = (p.Slot + 1) % p.SlotCount;

        string resp = server.Dispatch(Request(
            "{\"images\":[" + Image(p, seq, wrongSlot) + "]}"));

        Assert.Contains("does not match seq % slotCount", ErrorOf(resp));
        Assert.Empty(session.Emulator.Images);
        Assert.Empty(session.Emulator.Placements);
    }

    [Fact]
    public void Task3MappingRejectionsAreErrorRepliesAndLeaveTheSessionUsable()
    {
        var (server, session) = New();

        string RequestFor(string name, int slot = 0, long seq = 0, string extra = "") => Request(
            "{\"images\":[{\"id\":1,\"name\":" + JsonSerializer.Serialize(name) +
            ",\"slot\":" + slot + ",\"seq\":" + seq + extra + "}]}");

        string valid = NewRawMapping(RawHeader());
        string smallStride = NewRawMapping(RawHeader(
            slotStride: (Stride - 4) * Height, stride: Stride - 4));
        string overlappingSlots = NewRawMapping(RawHeader(
            slotStride: Stride * (Height - 1)));
        string outsideView = NewRawMapping(RawHeader(slotStride: 1L << 20));
        string negativeDimensions = NewRawMapping(RawHeader(width: -1));
        string overflowingDimensions = NewRawMapping(RawHeader(
            slotStride: 1L << 20, width: 0x10000, height: 0x10000, stride: 0x40000));
        string unsupportedFormat = NewRawMapping(RawHeader(format: (int)KittyFormat.Png));

        var rejected = new[]
        {
            RequestFor(@"Global\anything"),
            RequestFor(ShmFrameReader.NamePrefix + "missing-" + Guid.NewGuid().ToString("N")),
            RequestFor(valid, slot: 2),
            RequestFor(smallStride),
            RequestFor(overlappingSlots),
            RequestFor(outsideView, slot: 1),
            RequestFor(negativeDimensions),
            RequestFor(overflowingDimensions),
            RequestFor(unsupportedFormat),
            RequestFor(valid, seq: 2),
            RequestFor(valid, extra: ",\"width\":" + (Width + 1)),
        };

        foreach (string request in rejected)
        {
            Assert.NotEmpty(ErrorOf(server.Dispatch(request)));
            Assert.Empty(session.Emulator.Images);
            Assert.Empty(session.Emulator.Placements);
            Assert.Contains("\"ok\":true", server.Dispatch("{\"cmd\":\"ping\"}"));
        }

        // Windows rounds named sections to a page, so a view shorter than the 256-byte header is
        // unreachable through OpenExisting. RejectsAViewShorterThanTheHeader drives that guard via
        // the reader's narrowed-view overload; all reachable Task 3 failures go through JSON above.
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
        // it is why a pipelining producer must wait for the previous reply for each slot.
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
        // Three frames are published into distinct slots before any is drained. No slot is reused,
        // so this batch satisfies the spec's per-slot reply rule and every request gets its own.
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
