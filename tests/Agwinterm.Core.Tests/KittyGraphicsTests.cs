using System.Text;
using Agwinterm.Core;

namespace Agwinterm.Core.Tests;

public class KittyGraphicsTests
{
    private static TerminalEmulator Feed(string s)
    {
        var t = new TerminalEmulator(40, 10);
        t.Feed(Encoding.ASCII.GetBytes(s));
        return t;
    }

    // ESC _ G <control> ; <base64> ESC \    ( avoids C# greedy \x hex escapes)
    private static readonly string Esc = ((char)27).ToString();

    [Fact]
    public void Parser_RoutesApcSequence()
    {
        var rec = new VtParserTestsApcHelper();
        new VtParser(rec).Feed(Encoding.ASCII.GetBytes($"{Esc}_Ga=T,i=1;AAEC{Esc}\\X"));
        Assert.Equal("Ga=T,i=1;AAEC", rec.LastApc);
        Assert.Equal("X", rec.Printed); // parser returns to ground after ST
    }

    [Fact]
    public void TransmitAndDisplay_StoresImageAndPlacement()
    {
        // a=T transmit+display, id 7, RGB (f=24), 2x1 px, payload bytes {0,1,2} ("AAEC")
        var t = Feed($"{Esc}_Ga=T,i=7,f=24,s=2,v=1;AAEC{Esc}\\");
        Assert.True(t.Images.ContainsKey(7));
        var img = t.Images[7];
        Assert.Equal(KittyFormat.Rgb, img.Format);
        Assert.Equal(2, img.Width);
        Assert.Equal(1, img.Height);
        Assert.Equal(new byte[] { 0, 1, 2 }, img.Data);
        Assert.Single(t.Placements);
        Assert.Equal(7, t.Placements[0].ImageId);
    }

    [Fact]
    public void TransmitOnly_StoresImageWithoutPlacement()
    {
        var t = Feed($"{Esc}_Ga=t,i=3,f=32;AAEC{Esc}\\");
        Assert.True(t.Images.ContainsKey(3));
        Assert.Empty(t.Placements); // a=t does not display
    }

    [Fact]
    public void ChunkedTransmission_Reassembles()
    {
        // "AAECAwQF" = bytes 0..5, split across two chunks (m=1 then m=0).
        var t = Feed($"{Esc}_Ga=T,i=9,f=24,m=1;AAEC{Esc}\\{Esc}_Gm=0;AwQF{Esc}\\");
        Assert.True(t.Images.ContainsKey(9));
        Assert.Equal(new byte[] { 0, 1, 2, 3, 4, 5 }, t.Images[9].Data);
        Assert.Single(t.Placements);
    }

    [Fact]
    public void RepeatedDisplay_SameId_ReplacesPlacement()
    {
        var t = Feed($"{Esc}_Ga=T,i=5,f=24,s=1,v=1;AAEC{Esc}\\" +
                     $"{Esc}[3;3H{Esc}_Ga=p,i=5;{Esc}\\");
        Assert.Single(t.Placements);          // not duplicated
        Assert.Equal(2, t.Placements[0].Row); // moved to row 3 (idx 2)
        Assert.Equal(2, t.Placements[0].Col);
    }

    [Fact]
    public void DeleteAll_ClearsPlacements()
    {
        var t = Feed($"{Esc}_Ga=T,i=1,f=24,s=1,v=1;AAEC{Esc}\\{Esc}_Ga=d{Esc}\\");
        Assert.Empty(t.Placements);
    }

    [Fact]
    public void DeleteById_RemovesOnlyThatPlacement()
    {
        var t = Feed($"{Esc}_Ga=T,i=1,f=24,s=1,v=1;AAEC{Esc}\\" +
                     $"{Esc}[2;1H{Esc}_Ga=T,i=2,f=24,s=1,v=1;AAEC{Esc}\\" +
                     $"{Esc}_Ga=d,i=1{Esc}\\");
        Assert.Single(t.Placements);
        Assert.Equal(2, t.Placements[0].ImageId);
    }

    private sealed class VtParserTestsApcHelper : IParserPerformer
    {
        public string LastApc = "";
        public string Printed = "";
        public void Print(char ch) => Printed += ch;
        public void Execute(byte control) { }
        public void CsiDispatch(char final, IReadOnlyList<int> parameters, char prefix) { }
        public void EscDispatch(char final) { }
        public void OscDispatch(int command, string text) { }
        public void ApcDispatch(string data) => LastApc = data;
        public void DcsDispatch(byte[] data) { }
    }
}
