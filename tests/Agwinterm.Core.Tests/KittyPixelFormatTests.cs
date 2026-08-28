using System.Text;
using Agwinterm.Core;

namespace Agwinterm.Core.Tests;

/// <summary>
/// The host-only <see cref="KittyFormat.Bgra"/> and the upload path that selects on it. Two things
/// have to hold: BGRA pixels reach Direct2D without a channel swap (the point of the format), and
/// no APC sequence can mint the value, because a terminal-supplied "f=132" would otherwise send
/// RGBA bytes down the no-swizzle route and render with red and blue exchanged.
/// </summary>
public class KittyPixelFormatTests
{
    private static readonly string Esc = ((char)27).ToString();

    private static TerminalEmulator Feed(string s)
    {
        var t = new TerminalEmulator(40, 10);
        t.Feed(Encoding.ASCII.GetBytes(s));
        return t;
    }

    [Fact]
    public void Bgra_SitsOutsideTheKittyWireRange()
    {
        Assert.Equal(132, (int)KittyFormat.Bgra);
        Assert.False(KittyFormats.IsWireFormat((int)KittyFormat.Bgra));
        Assert.True(KittyFormats.IsWireFormat(24));
        Assert.True(KittyFormats.IsWireFormat(32));
        Assert.True(KittyFormats.IsWireFormat(100));
    }

    [Theory]
    [InlineData(24, KittyFormat.Rgb)]
    [InlineData(32, KittyFormat.Rgba)]
    [InlineData(100, KittyFormat.Png)]
    [InlineData(132, KittyFormat.Rgba)]   // the host-only value falls back
    [InlineData(0, KittyFormat.Rgba)]
    [InlineData(-1, KittyFormat.Rgba)]
    [InlineData(99999, KittyFormat.Rgba)]
    public void ParseWireFormat_ClampsToTheProtocolFormats(int f, KittyFormat expected)
        => Assert.Equal(expected, KittyFormats.ParseWireFormat(f));

    [Fact]
    public void Upload_Bgra_TakesTheNoSwizzleRoute()
    {
        // One opaque pixel, already in target order: B=10, G=20, R=30, A=255. It must come out
        // byte-for-byte, not reversed.
        var img = new KittyImage(1, KittyFormat.Bgra, 1, 1, new byte[] { 10, 20, 30, 255 });
        Assert.Equal(new byte[] { 10, 20, 30, 255 }, KittyPixels.ToPremultipliedBgra(img));
    }

    [Fact]
    public void Upload_Rgba_SwizzlesRedAndBlue()
    {
        // Same four bytes read as RGBA: R=10, G=20, B=30 -> BGRA is 30, 20, 10.
        var img = new KittyImage(1, KittyFormat.Rgba, 1, 1, new byte[] { 10, 20, 30, 255 });
        Assert.Equal(new byte[] { 30, 20, 10, 255 }, KittyPixels.ToPremultipliedBgra(img));
    }

    [Fact]
    public void Upload_BgraAndRgba_DifferExactlyByTheSwap()
    {
        var pixels = new byte[] { 1, 2, 3, 255, 40, 50, 60, 255 };
        var bgra = KittyPixels.ToPremultipliedBgra(new KittyImage(1, KittyFormat.Bgra, 2, 1, pixels));
        var rgba = KittyPixels.ToPremultipliedBgra(new KittyImage(1, KittyFormat.Rgba, 2, 1, pixels));
        for (int j = 0; j < bgra.Length; j += 4)
        {
            Assert.Equal(bgra[j], rgba[j + 2]);
            Assert.Equal(bgra[j + 1], rgba[j + 1]);
            Assert.Equal(bgra[j + 2], rgba[j]);
            Assert.Equal(bgra[j + 3], rgba[j + 3]);
        }
    }

    [Fact]
    public void Upload_Bgra_PremultipliesAlpha()
    {
        // Straight alpha in, premultiplied out — D2D's render target is Premultiplied.
        var img = new KittyImage(1, KittyFormat.Bgra, 1, 1, new byte[] { 200, 100, 50, 128 });
        Assert.Equal(new byte[] { (byte)(200 * 128 / 255), (byte)(100 * 128 / 255), (byte)(50 * 128 / 255), 128 },
            KittyPixels.ToPremultipliedBgra(img));
    }

    [Fact]
    public void Upload_Rgb_ExpandsToOpaqueBgra()
    {
        var img = new KittyImage(1, KittyFormat.Rgb, 1, 1, new byte[] { 10, 20, 30 });
        Assert.Equal(new byte[] { 30, 20, 10, 255 }, KittyPixels.ToPremultipliedBgra(img));
    }

    [Fact]
    public void Upload_ShortPayload_DoesNotReadOutOfBounds()
    {
        // A lying producer declares 4x4 and supplies one pixel: the tail stays zeroed.
        var img = new KittyImage(1, KittyFormat.Bgra, 4, 4, new byte[] { 1, 2, 3, 255 });
        var outb = KittyPixels.ToPremultipliedBgra(img);
        Assert.Equal(4 * 4 * 4, outb.Length);
        Assert.Equal(new byte[] { 1, 2, 3, 255 }, outb[..4]);
        Assert.All(outb[4..], b => Assert.Equal(0, b));
    }

    [Fact]
    public void Upload_NegativeDimensions_ReturnEmptyRatherThanThrow()
    {
        var img = new KittyImage(1, KittyFormat.Bgra, -8, 4, new byte[] { 1, 2, 3, 255 });
        Assert.Empty(KittyPixels.ToPremultipliedBgra(img));
    }

    [Fact]
    public void CraftedApc_DeclaringF132_DoesNotProduceBgra()
    {
        // The attack this guards: terminal output that names the host-only format so the renderer
        // skips the swizzle on bytes that are actually RGBA.
        var t = Feed($"{Esc}_Ga=T,i=11,f=132,s=1,v=1;AAECAw=={Esc}\\");
        Assert.True(t.Images.ContainsKey(11));
        Assert.NotEqual(KittyFormat.Bgra, t.Images[11].Format);
        Assert.Equal(KittyFormat.Rgba, t.Images[11].Format);   // clamped to the protocol default
    }

    [Fact]
    public void CraftedApc_OtherOutOfRangeFormats_AlsoClampToRgba()
    {
        var t = Feed($"{Esc}_Ga=T,i=12,f=7,s=1,v=1;AAECAw=={Esc}\\");
        Assert.Equal(KittyFormat.Rgba, t.Images[12].Format);
    }

    [Fact]
    public void Apc_WireFormats_StillRoundTrip()
    {
        // The clamp must not disturb the three legal values.
        Assert.Equal(KittyFormat.Rgb, Feed($"{Esc}_Ga=t,i=1,f=24,s=1,v=1;AAEC{Esc}\\").Images[1].Format);
        Assert.Equal(KittyFormat.Rgba, Feed($"{Esc}_Ga=t,i=2,f=32,s=1,v=1;AAECAw=={Esc}\\").Images[2].Format);
        Assert.Equal(KittyFormat.Png, Feed($"{Esc}_Ga=t,i=3,f=100;AAEC{Esc}\\").Images[3].Format);
    }

    [Fact]
    public void HostPath_CanStillSetBgraDirectly()
    {
        // The parser is gated; the host API (what image.frameshm uses) is not.
        var t = new TerminalEmulator(40, 10);
        t.SetImageData(20, KittyFormat.Bgra, 1, 1, new byte[] { 1, 2, 3, 255 });
        Assert.Equal(KittyFormat.Bgra, t.Images[20].Format);
    }
}
