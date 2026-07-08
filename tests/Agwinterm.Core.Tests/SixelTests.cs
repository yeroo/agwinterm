using System.Text;
using Agwinterm.Core;

namespace Agwinterm.Core.Tests;

public class SixelTests
{
    [Fact]
    public void Decode_SimpleTwoColorSixel()
    {
        // "#0;2;100;0;0" defines color 0 = red (RGB 100%,0,0); "@" (0x40) = bit 0 set (top pixel);
        // then color 1 green, "~" (0x7E) = bits 0..5 all set (full 6-pixel column).
        var payload = "#0;2;100;0;0#0@#1;2;0;100;0#1~";
        var (w, h, rgba) = Sixel.Decode(ToDcs(payload))!.Value;
        Assert.True(w >= 2 && h >= 6);
        // Col 0, row 0 = red (from '@' with color 0).
        int o0 = (0 * w + 0) * 4;
        Assert.Equal(255, rgba[o0]); Assert.Equal(0, rgba[o0 + 1]); Assert.Equal(0, rgba[o0 + 2]); Assert.Equal(255, rgba[o0 + 3]);
        // Col 1, rows 0..5 = green (from '~' with color 1).
        for (int row = 0; row < 6; row++)
        {
            int o = (row * w + 1) * 4;
            Assert.Equal(0, rgba[o]); Assert.Equal(255, rgba[o + 1]); Assert.Equal(0, rgba[o + 2]);
        }
    }

    [Fact]
    public void Decode_RepeatAndNewline()
    {
        // "!5~" repeats a full column 5 times; "-" moves to the next band; then another column.
        var (w, h, _) = Sixel.Decode(ToDcs("#0;2;0;0;100!5~-@"))!.Value;
        Assert.True(w >= 5);
        Assert.True(h >= 7);   // band 0 (6px) + band 1 has a pixel -> at least 7
    }

    [Fact]
    public void Emulator_SixelPlacesAnImage()
    {
        var t = new TerminalEmulator(40, 10);
        t.Feed(Encoding.UTF8.GetBytes("\x1bP0;0;0q#0;2;100;0;0~~~~\x1b\\"));
        Assert.Single(t.Placements);
        var pl = t.Placements[0];
        Assert.True(t.Images.ContainsKey(pl.ImageId));
        Assert.True(pl.ImageId < 0);   // sixel-generated id
    }

    private static byte[] ToDcs(string sixelBody)
        => Encoding.ASCII.GetBytes("0;0;0q" + sixelBody);   // params + 'q' + body (as DcsDispatch receives)
}
