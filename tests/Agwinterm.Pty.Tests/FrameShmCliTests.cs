using System.Text.Json;
using Agwinterm.Ctl;
using Xunit;

namespace Agwinterm.Pty.Tests;

/// <summary>
/// `agwintermctl image frameshm` arg parsing. The point of these is the JSON *types*: ControlServer
/// answers "'slot' must be a JSON number, not string" for a quoted number, so a CLI that shipped
/// strings would fail every call with a message that blames the producer.
/// </summary>
public class FrameShmCliTests
{
    private const string Name = @"Local\agwinterm-frame-browser-1";

    private static Dictionary<string, string> Opts(params (string k, string v)[] pairs)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in pairs) d[k] = v;
        return d;
    }

    /// <summary>Serialize exactly as Program.cs does, so the assertions see the real wire bytes.</summary>
    private static string Wire(Dictionary<string, object?> cargs) => JsonSerializer.Serialize(cargs);

    private static JsonElement FirstImage(Dictionary<string, object?> cargs) =>
        JsonDocument.Parse(Wire(cargs)).RootElement.GetProperty("images")[0].Clone();

    [Fact]
    public void ANameAloneBuildsASingleEntryImagesArray()
    {
        Assert.True(FrameShmCli.TryBuildArgs([Name], Opts(), out var cargs, out var err));
        Assert.Null(err);
        var img = FirstImage(cargs);
        Assert.Equal(Name, img.GetProperty("name").GetString());
        // No numeric flags given -> no numeric properties at all, so the server applies its defaults
        // rather than being told an explicit 0 the caller never asked for.
        Assert.Single(img.EnumerateObject());
    }

    [Fact]
    public void EveryNumericOptionSerialisesAsAJsonNumber()
    {
        var opts = Opts();
        int i = 1;
        foreach (string f in FrameShmCli.NumericFields) opts[f] = (i++).ToString();

        Assert.True(FrameShmCli.TryBuildArgs([Name], opts, out var cargs, out var err));
        Assert.Null(err);

        var img = FirstImage(cargs);
        i = 1;
        foreach (string f in FrameShmCli.NumericFields)
        {
            var v = img.GetProperty(f);
            Assert.Equal(JsonValueKind.Number, v.ValueKind);
            Assert.Equal(i++, v.GetInt32());
        }
    }

    [Fact]
    public void TheWireTextCarriesUnquotedNumbers()
    {
        Assert.True(FrameShmCli.TryBuildArgs([Name], Opts(("slot", "1"), ("seq", "42")), out var cargs, out _));
        string json = Wire(cargs);
        Assert.Contains("\"slot\":1", json);
        Assert.Contains("\"seq\":42", json);
        Assert.DoesNotContain("\"slot\":\"1\"", json);
        Assert.DoesNotContain("\"seq\":\"42\"", json);
    }

    [Fact]
    public void ParsedArgsAreAcceptedByTheControlServersNumericValidation()
    {
        // The contract this whole class exists for: what the CLI emits parses as the server reads it.
        Assert.True(FrameShmCli.TryBuildArgs(
            [Name], Opts(("slot", "1"), ("seq", "7"), ("width", "4"), ("height", "2"),
                         ("stride", "16"), ("format", "132"), ("cols", "10"), ("rows", "3")),
            out var cargs, out _));

        var img = FirstImage(cargs);
        foreach (string f in new[] { "slot", "seq", "width", "height", "stride", "format", "cols", "rows" })
            Assert.Equal(JsonValueKind.Number, img.GetProperty(f).ValueKind);
        Assert.Equal(132, img.GetProperty("format").GetInt32());
    }

    [Fact]
    public void ANegativeNumberIsPassedThroughForTheServerToReject()
    {
        // The CLI's job is types, not ranges; -1 is a number and the server owns what it means.
        Assert.True(FrameShmCli.TryBuildArgs([Name], Opts(("slot", "-1")), out var cargs, out _));
        Assert.Equal(-1, FirstImage(cargs).GetProperty("slot").GetInt32());
    }

    [Fact]
    public void SeqAcceptsAValueBeyond32Bits()
    {
        // seq is a 64-bit publish counter on the server; a long-running producer outlives an int.
        Assert.True(FrameShmCli.TryBuildArgs([Name], Opts(("seq", "5000000000")), out var cargs, out var err));
        Assert.Null(err);
        Assert.Equal(5_000_000_000L, FirstImage(cargs).GetProperty("seq").GetInt64());
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("1.5")]
    [InlineData("")]
    [InlineData("0x10")]
    public void ANonNumericOptionIsRejectedLocally(string raw)
    {
        Assert.False(FrameShmCli.TryBuildArgs([Name], Opts(("slot", raw)), out _, out var err));
        Assert.Contains("--slot", err);
        Assert.Contains("whole number", err);
    }

    [Fact]
    public void AWidthBeyond32BitsIsRejectedByFlagName()
    {
        Assert.False(FrameShmCli.TryBuildArgs([Name], Opts(("width", "5000000000")), out _, out var err));
        Assert.Contains("--width", err);
        Assert.Contains("32-bit", err);
    }

    [Fact]
    public void AValuelessFlagIsIgnoredRatherThanParsedAsTrue()
    {
        // The option splitter stores "true" for a flag with no value; that must not become a number.
        Assert.True(FrameShmCli.TryBuildArgs([Name], Opts(("slot", "true")), out var cargs, out var err));
        Assert.Null(err);
        Assert.False(FirstImage(cargs).TryGetProperty("slot", out _));
    }

    [Fact]
    public void AMissingNameIsRejected()
    {
        Assert.False(FrameShmCli.TryBuildArgs([], Opts(), out _, out var err));
        Assert.Contains("mapping name", err);
    }

    [Theory]
    [InlineData(@"Global\agwinterm-frame-x")]
    [InlineData(@"local\agwinterm-frame-x")]
    [InlineData("agwinterm-frame-x")]
    [InlineData(@"Local\something-else")]
    public void ANameOutsideThePrefixIsRejectedBeforeThePipeIsOpened(string name)
    {
        Assert.False(FrameShmCli.TryBuildArgs([name], Opts(), out _, out var err));
        Assert.Contains(FrameShmCli.NamePrefix, err);
    }

    [Fact]
    public void ExtraPositionalsAreRejectedRatherThanSilentlyDropped()
    {
        Assert.False(FrameShmCli.TryBuildArgs([Name, "extra"], Opts(), out _, out var err));
        Assert.Contains("--images", err);
    }

    [Fact]
    public void ImagesJsonIsPassedThroughVerbatim()
    {
        string raw = """[{"name":"Local\\agwinterm-frame-a","slot":0,"seq":3},{"name":"Local\\agwinterm-frame-b","slot":1,"seq":3}]""";
        Assert.True(FrameShmCli.TryBuildArgs([], Opts(("images", raw)), out var cargs, out var err));
        Assert.Null(err);

        var arr = JsonDocument.Parse(Wire(cargs)).RootElement.GetProperty("images");
        Assert.Equal(2, arr.GetArrayLength());
        Assert.Equal(@"Local\agwinterm-frame-b", arr[1].GetProperty("name").GetString());
        Assert.Equal(JsonValueKind.Number, arr[1].GetProperty("seq").ValueKind);
    }

    [Fact]
    public void ImagesAndAPositionalNameTogetherAreRejected()
    {
        Assert.False(FrameShmCli.TryBuildArgs([Name], Opts(("images", "[]")), out _, out var err));
        Assert.Contains("not both", err);
    }

    [Fact]
    public void MalformedImagesJsonIsRejected()
    {
        Assert.False(FrameShmCli.TryBuildArgs([], Opts(("images", "[{")), out _, out var err));
        Assert.Contains("valid JSON", err);
    }

    [Fact]
    public void ImagesThatIsNotAnArrayIsRejected()
    {
        Assert.False(FrameShmCli.TryBuildArgs([], Opts(("images", "{\"name\":\"x\"}")), out _, out var err));
        Assert.Contains("JSON array", err);
    }
}
