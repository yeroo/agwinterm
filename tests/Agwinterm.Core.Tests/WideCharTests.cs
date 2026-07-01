using System.Text;
using Agwinterm.Core;

namespace Agwinterm.Core.Tests;

public class WideCharTests
{
    // CJK / combining code points spelled out to avoid source-encoding ambiguity.
    private const string Zhong = "中";          // 中 (wide)
    private const string CombiningAcute = "́"; // zero-width combining mark

    private static TerminalEmulator Feed(int cols, int rows, string s)
    {
        var t = new TerminalEmulator(cols, rows);
        t.Feed(Encoding.UTF8.GetBytes(s));
        return t;
    }

    [Fact]
    public void Wcwidth_ClassifiesCommonCases()
    {
        Assert.Equal(1, Wcwidth.Of(0x0061)); // 'a'
        Assert.Equal(1, Wcwidth.Of(0x00E9)); // precomposed é
        Assert.Equal(2, Wcwidth.Of(0x4E2D)); // 中 CJK ideograph
        Assert.Equal(2, Wcwidth.Of(0x65E5)); // 日
        Assert.Equal(2, Wcwidth.Of(0xAC00)); // 가 Hangul syllable
        Assert.Equal(0, Wcwidth.Of(0x0301)); // combining acute accent
    }

    [Fact]
    public void WideChar_OccupiesTwoCells()
    {
        var t = Feed(10, 2, Zhong);
        Assert.Equal(0x4E2D, t.Screen[0, 0].Rune);
        Assert.Equal(2, t.Screen[0, 0].Width);
        Assert.Equal(0, t.Screen[0, 1].Width); // trailing spacer
        Assert.Equal(2, t.CursorCol);          // advanced by 2
    }

    [Fact]
    public void MixedNarrowAndWide_PositionsCorrectly()
    {
        var t = Feed(10, 2, "a" + Zhong + "b");
        Assert.Equal('a', t.Screen[0, 0].Rune);
        Assert.Equal(0x4E2D, t.Screen[0, 1].Rune);
        Assert.Equal(0, t.Screen[0, 2].Width); // spacer
        Assert.Equal('b', t.Screen[0, 3].Rune);
        Assert.Equal(4, t.CursorCol);
        Assert.Equal("a" + Zhong + "b", t.DumpRow(0));
    }

    [Fact]
    public void WideChar_AtRightEdge_WrapsToNextLine()
    {
        // 3 cols: "ab" fills the first two; the wide char can't fit the last column -> wraps.
        var t = Feed(3, 3, "ab" + Zhong);
        Assert.Equal("ab", t.DumpRow(0));
        Assert.Equal(Zhong, t.DumpRow(1));
    }

    [Fact]
    public void CombiningMark_IsDropped_NoLayoutCorruption()
    {
        var t = Feed(10, 2, "a" + CombiningAcute + "b");
        Assert.Equal("ab", t.DumpRow(0)); // combining mark dropped (v1), layout intact
        Assert.Equal(2, t.CursorCol);
    }
}
