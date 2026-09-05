using Agwinterm.Pty;

namespace Agwinterm.Pty.Tests;

/// <summary>
/// THE PER-WINDOW RESTORE FILE — windows\&lt;window-id&gt;.json — AS A FORMAT (P3).
///
/// Before P3 nothing under tests/ could reach the POCOs (they were private to the Win32 host) and the
/// only persistence assertion in the repo grepped a JSON file for <c>"SidebarWidth": 320</c>. The rule
/// at the top of <see cref="RestoreState"/> — additive keys, no version, unknown keys ignored, older
/// builds drop them on write-back, every loaded value validated — is what these tests pin, so P4
/// (split axis) and P7 change the file with a round-trip beside them rather than a grep.
///
/// The fixture below is byte-for-byte what 0.17.11 writes for a one-workspace, one-session tree (the
/// indentation and the key order are System.Text.Json's with <c>WriteIndented</c>); the "old shape"
/// tests are the write-back comparison stated as such: a P3 build reading it and saving it back must
/// produce the same bytes, and a P3 build saving a session WITHOUT a context must produce what a
/// pre-P3 build produces for that session — the key is absent, not <c>null</c>.
/// </summary>
public class RestoreStateTests
{
    /// <summary>Exactly what a 0.17.11 build saves for one workspace with one single-pane session — with the
    /// platform newline (WriteIndented indents with Environment.NewLine, so the file is CRLF on Windows).</summary>
    private static readonly string PreP3File = N(PreP3Literal);

    /// <summary>LF → the platform newline, so the fixture and the tests that splice keys into it can be
    /// written with <c>\n</c>.</summary>
    private static string N(string lf) => lf.Replace("\n", Environment.NewLine);

    private const string PreP3Literal =
        "{\n" +
        "  \"Workspaces\": [\n" +
        "    {\n" +
        "      \"Id\": \"ws-1\",\n" +
        "      \"Name\": \"workspace 1\",\n" +
        "      \"Expanded\": true,\n" +
        "      \"Sessions\": [\n" +
        "        {\n" +
        "          \"Id\": \"ses-1\",\n" +
        "          \"Name\": \"build\",\n" +
        "          \"CustomName\": \"build\",\n" +
        "          \"Profile\": \"Windows PowerShell\",\n" +
        "          \"Active\": 0,\n" +
        "          \"Flagged\": true,\n" +
        "          \"Panes\": [\n" +
        "            {\n" +
        "              \"Id\": \"ses-1\",\n" +
        "              \"Cwd\": \"C:\\\\Users\\\\boris\\\\source\",\n" +
        "              \"FontSize\": 16,\n" +
        "              \"Ratio\": 1,\n" +
        "              \"Command\": \"\",\n" +
        "              \"AgentResume\": null,\n" +
        "              \"RestoreCommand\": \"cargo watch\",\n" +
        "              \"Buffer\": null,\n" +
        "              \"BufferBlob\": null\n" +
        "            }\n" +
        "          ],\n" +
        "          \"Cwd\": \"\",\n" +
        "          \"FontSize\": 0,\n" +
        "          \"BgFile\": null,\n" +
        "          \"BgOpacity\": 15,\n" +
        "          \"BgMode\": \"fit\"\n" +
        "        }\n" +
        "      ]\n" +
        "    }\n" +
        "  ],\n" +
        "  \"ActiveId\": \"ses-1\",\n" +
        "  \"SidebarWidth\": 320,\n" +
        "  \"SidebarVisible\": true,\n" +
        "  \"WindowX\": 100,\n" +
        "  \"WindowY\": 50,\n" +
        "  \"WindowWidth\": 1280,\n" +
        "  \"WindowHeight\": 800,\n" +
        "  \"WindowMaximized\": false,\n" +
        "  \"SidebarMode\": \"tree\",\n" +
        "  \"FocusedWorkspaceId\": null,\n" +
        "  \"Mru\": [\n" +
        "    \"ses-1\"\n" +
        "  ]\n" +
        "}";

    /// <summary>The same tree built in memory (so the fixture and the POCOs are checked against each other).</summary>
    private static AppState Tree(string? context = null) => new()
    {
        Workspaces =
        {
            new WorkspaceState
            {
                Id = "ws-1", Name = "workspace 1", Expanded = true,
                Sessions =
                {
                    new SessionState
                    {
                        Id = "ses-1", Name = "build", CustomName = "build", Context = context, Profile = "Windows PowerShell",
                        Active = 0, Flagged = true,
                        Panes = { new PaneState { Id = "ses-1", Cwd = @"C:\Users\boris\source", FontSize = 16, Ratio = 1, Command = "", RestoreCommand = "cargo watch" } },
                    }
                }
            }
        },
        ActiveId = "ses-1", SidebarWidth = 320, SidebarVisible = true,
        WindowX = 100, WindowY = 50, WindowWidth = 1280, WindowHeight = 800, WindowMaximized = false,
        SidebarMode = "tree", FocusedWorkspaceId = null, Mru = { "ses-1" },
    };

    private static AppState Load(string json)
    {
        Assert.True(RestoreState.TryDeserialize(json, out var st), "the file did not parse");
        Assert.NotNull(st);
        return st!;
    }

    private static void AssertEveryOtherFieldIntact(AppState st)
    {
        var ws = Assert.Single(st.Workspaces);
        Assert.Equal("ws-1", ws.Id); Assert.Equal("workspace 1", ws.Name); Assert.True(ws.Expanded);
        var s = Assert.Single(ws.Sessions);
        Assert.Equal("ses-1", s.Id); Assert.Equal("build", s.Name); Assert.Equal("build", s.CustomName);
        Assert.Equal("Windows PowerShell", s.Profile); Assert.Equal(0, s.Active); Assert.True(s.Flagged);
        Assert.Equal("", s.Cwd); Assert.Equal(0f, s.FontSize); Assert.Null(s.BgFile); Assert.Equal(15, s.BgOpacity); Assert.Equal("fit", s.BgMode);
        var p = Assert.Single(s.Panes);
        Assert.Equal("ses-1", p.Id); Assert.Equal(@"C:\Users\boris\source", p.Cwd); Assert.Equal(16f, p.FontSize); Assert.Equal(1f, p.Ratio);
        Assert.Equal("", p.Command); Assert.Null(p.AgentResume); Assert.Equal("cargo watch", p.RestoreCommand); Assert.Null(p.Buffer); Assert.Null(p.BufferBlob);
        Assert.Equal("ses-1", st.ActiveId); Assert.Equal(320f, st.SidebarWidth); Assert.True(st.SidebarVisible);
        Assert.Equal(100, st.WindowX); Assert.Equal(50, st.WindowY); Assert.Equal(1280, st.WindowWidth); Assert.Equal(800, st.WindowHeight); Assert.False(st.WindowMaximized);
        Assert.Equal("tree", st.SidebarMode); Assert.Null(st.FocusedWorkspaceId); Assert.Equal(new[] { "ses-1" }, st.Mru);
    }

    // ---- the move: a pre-P3 file is the serializer's own output ----

    [Fact]
    public void PreP3File_RoundTripsByteForByte()
    {
        // A P3 build reading a 0.17.11 file and saving it back writes the same bytes: same keys, same
        // order, same indentation, and NO Context key for a session that has none.
        var st = Load(PreP3File);
        Assert.Equal(PreP3File, RestoreState.Serialize(st));
    }

    [Fact]
    public void InMemoryTree_SerializesToThePreP3Bytes()
    {
        // The POCOs and the fixture agree — so the fixture is not just self-consistent, it is what the
        // host's SaveState produces for that tree.
        Assert.Equal(PreP3File, RestoreState.Serialize(Tree()));
    }

    // ---- Context ----

    [Fact]
    public void RoundTrip_PreservesContext()
    {
        string json = RestoreState.Serialize(Tree("build the persistence batch"));
        Assert.Contains("\"Context\": \"build the persistence batch\"", json);
        var st = Load(json);
        Assert.Equal("build the persistence batch", st.Workspaces[0].Sessions[0].Context);
        AssertEveryOtherFieldIntact(st);
    }

    [Fact]
    public void Context_IsWrittenBesideCustomName()
    {
        // Key order is the file's order; the new key sits where it reads naturally, and nothing
        // else moved (the pre-P3 bytes test pins the rest).
        string json = RestoreState.Serialize(Tree("ctx"));
        int name = json.IndexOf("\"CustomName\"", StringComparison.Ordinal);
        int ctx = json.IndexOf("\"Context\"", StringComparison.Ordinal);
        int profile = json.IndexOf("\"Profile\"", StringComparison.Ordinal);
        Assert.True(name < ctx && ctx < profile, json);
    }

    [Fact]
    public void OldShapeFile_LoadsWithNullContext_EveryOtherFieldIntact()
    {
        var st = Load(PreP3File);
        Assert.Null(st.Workspaces[0].Sessions[0].Context);
        AssertEveryOtherFieldIntact(st);
    }

    [Fact]
    public void NullContext_SerializesAsWhatAPreP3BuildWrites()
    {
        // The write-back comparison, stated as such: a pre-P3 build has no Context property, so for a
        // session without one it writes no key. A P3 build with Context = null must write the same —
        // not "Context": null — or every file saved by P3 differs from every file saved before it,
        // and a downgrade (an older build dropping the key on write-back) would look like a change.
        string json = RestoreState.Serialize(Tree(context: null));
        Assert.DoesNotContain("Context", json);
        Assert.Equal(PreP3File, json);
    }

    // ---- unknown keys: the additive rule, both directions ----

    [Fact]
    public void UnknownKeys_AtEveryLevel_AreIgnoredOnRead()
    {
        // A file written by a NEWER build (P4's split axis, say) has keys this build does not know.
        // Tolerance on read is what makes additive-only safe; the keys are dropped, not fatal.
        string json = N(PreP3Literal
            .Replace("  \"ActiveId\": \"ses-1\",\n", "  \"ActiveId\": \"ses-1\",\n  \"SplitAxis\": \"horizontal\",\n")
            .Replace("      \"Expanded\": true,\n", "      \"Expanded\": true,\n      \"Colour\": \"teal\",\n")
            .Replace("          \"Flagged\": true,\n", "          \"Flagged\": true,\n          \"Hud\": { \"a\": 1 },\n")
            .Replace("              \"Ratio\": 1,\n", "              \"Ratio\": 1,\n              \"Axis\": 2,\n"));
        Assert.NotEqual(PreP3File, json);
        var st = Load(json);
        Assert.Null(st.Workspaces[0].Sessions[0].Context);
        AssertEveryOtherFieldIntact(st);
        // ...and the write-back drops them: this is the loss the format rule names, made visible.
        Assert.Equal(PreP3File, RestoreState.Serialize(st));
    }

    [Fact]
    public void MissingKeys_TakeTheirDefaults()
    {
        // A file written by an OLDER build lacks keys this build has (a pre-Mru, pre-SidebarMode file).
        var st = Load("{\"Workspaces\":[{\"Id\":\"w\",\"Sessions\":[{\"Id\":\"s\",\"Name\":\"n\"}]}]}");
        Assert.Equal(SidebarWidths.Default, st.SidebarWidth);
        Assert.True(st.SidebarVisible);
        Assert.Equal("tree", st.SidebarMode);
        Assert.Empty(st.Mru);
        var s = st.Workspaces[0].Sessions[0];
        Assert.Null(s.Context); Assert.Null(s.CustomName); Assert.Empty(s.Panes); Assert.Equal(15, s.BgOpacity); Assert.Equal("fit", s.BgMode);
        Assert.True(st.Workspaces[0].Expanded);
    }

    [Fact]
    public void BrokenJson_IsTheOnlyBadFile()
    {
        // A parse failure is the one "bad file" path (the host renames it .bad); the cases above never reach it.
        Assert.False(RestoreState.TryDeserialize("{\"Workspaces\": [", out var st));
        Assert.Null(st);
        Assert.False(RestoreState.TryDeserialize("", out _));
    }

    // ---- validated on load, not trusted ----

    [Theory]
    [InlineData("build the batch", "build the batch")]
    [InlineData("  padded  ", "padded")]                 // normalised, as the verb normalises
    [InlineData("two\nlines", null)]                     // a newline in a title bar is a rendering accident
    [InlineData("tab\there", null)]
    [InlineData("esc\u001b[31m", null)]
    [InlineData("nel\u0085", null)]                      // U+0085 is whitespace to .NET and a control to a title bar
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData(null, null)]
    public void LoadContext_DropsWhatTheVerbWouldRefuse(string? stored, string? expected)
    {
        Assert.Equal(expected, RestoreState.LoadContext(stored));
    }

    [Fact]
    public void LoadContext_CeilingIsTheVerbs()
    {
        Assert.Equal(new string('x', SessionContexts.MaxLength), RestoreState.LoadContext(new string('x', SessionContexts.MaxLength)));
        Assert.Null(RestoreState.LoadContext(new string('x', SessionContexts.MaxLength + 1)));
    }

    [Fact]
    public void OutOfRulesContext_InAFile_SurvivesTheParseAndIsDroppedOnLoad()
    {
        // A hand-edited (or foreign) file with a newline in the context: JSON parses it fine — the
        // POCO carries the raw value, so a round-trip does not launder it — and the loader drops it.
        string json = N(PreP3Literal.Replace("\"CustomName\": \"build\",\n", "\"CustomName\": \"build\",\n          \"Context\": \"line one\\nline two\",\n"));
        var st = Load(json);
        Assert.Equal("line one\nline two", st.Workspaces[0].Sessions[0].Context);
        Assert.Null(RestoreState.LoadContext(st.Workspaces[0].Sessions[0].Context));
        AssertEveryOtherFieldIntact(st);
    }
}
