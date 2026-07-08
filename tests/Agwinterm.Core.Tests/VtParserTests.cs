using System.Text;
using Agwinterm.Core;

namespace Agwinterm.Core.Tests;

public class VtParserTests
{
    private sealed class Recorder : IParserPerformer
    {
        public readonly List<string> Events = new();
        public void Print(char ch) => Events.Add($"print:{ch}");
        public void Execute(byte control) => Events.Add($"exec:{control}");
        public void CsiDispatch(char final, IReadOnlyList<int> p, char prefix) =>
            Events.Add($"csi:{(prefix != '\0' ? prefix.ToString() : "")}{final}:{string.Join(',', p)}");
        public void EscDispatch(char final) => Events.Add($"esc:{final}");
        public void OscDispatch(int command, string text) => Events.Add($"osc:{command}:{text}");
        public void ApcDispatch(string data) => Events.Add($"apc:{data}");
        public void DcsDispatch(byte[] data) => Events.Add($"dcs:{data.Length}");
    }

    private static Recorder Run(string input)
    {
        var rec = new Recorder();
        new VtParser(rec).Feed(Encoding.ASCII.GetBytes(input));
        return rec;
    }

    [Fact]
    public void PrintsPlainText()
    {
        var rec = Run("Hi");
        Assert.Equal(new[] { "print:H", "print:i" }, rec.Events);
    }

    [Fact]
    public void ExecutesC0Control()
    {
        var rec = Run("A\nB");
        Assert.Equal(new[] { "print:A", "exec:10", "print:B" }, rec.Events);
    }

    [Fact]
    public void ParsesCsiWithParameters()
    {
        var rec = Run("\x1b[1;31m");
        Assert.Equal(new[] { "csi:m:1,31" }, rec.Events);
    }

    [Fact]
    public void CsiNoParams_DispatchesEmpty()
    {
        var rec = Run("\x1b[H");
        Assert.Equal(new[] { "csi:H:" }, rec.Events);
    }

    [Fact]
    public void ParsesEscFinal()
    {
        var rec = Run("\x1bM");
        Assert.Equal(new[] { "esc:M" }, rec.Events);
    }

    private static List<string> RunBytes(params byte[] bytes)
    {
        var rec = new Recorder();
        new VtParser(rec).Feed(bytes);
        return rec.Events;
    }

    [Fact]
    public void DecodesTwoByteUtf8()
    {
        // U+00E9 é = 0xC3 0xA9
        Assert.Equal(new[] { "print:é" }, RunBytes(0xC3, 0xA9));
    }

    [Fact]
    public void DecodesThreeByteUtf8()
    {
        // U+4E2D 中 = 0xE4 0xB8 0xAD
        Assert.Equal(new[] { "print:中" }, RunBytes(0xE4, 0xB8, 0xAD));
    }

    [Fact]
    public void Utf8MixedWithAscii()
    {
        // "aé b" with é multibyte
        Assert.Equal(new[] { "print:a", "print:é", "print: ", "print:b" },
            RunBytes((byte)'a', 0xC3, 0xA9, (byte)' ', (byte)'b'));
    }

    [Fact]
    public void AstralCodepoint_EmitsSurrogatePair()
    {
        // U+1F600 😀 = 0xF0 0x9F 0x98 0x80 -> the performer receives the surrogate pair
        // (the emulator re-pairs it into a single cell).
        Assert.Equal(new[] { "print:\uD83D", "print:\uDE00" }, RunBytes(0xF0, 0x9F, 0x98, 0x80));
    }

    [Fact]
    public void OscBelTerminated()
    {
        var rec = Run("\x1b]0;my title\x07X");
        Assert.Equal(new[] { "osc:0:my title", "print:X" }, rec.Events);
    }

    [Fact]
    public void OscStTerminated()
    {
        // ESC \ String Terminator
        var rec = Run("\x1b]7;file:///c:/tmp\x1b\\Y");
        Assert.Equal(new[] { "osc:7:file:///c:/tmp", "print:Y" }, rec.Events);
    }

    [Fact]
    public void CsiPrivateMarkerCaptured()
    {
        var rec = Run("\x1b[?1049h");
        Assert.Equal(new[] { "csi:?h:1049" }, rec.Events);
    }
}
