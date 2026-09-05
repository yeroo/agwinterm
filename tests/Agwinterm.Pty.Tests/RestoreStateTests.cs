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
        // A file written by a NEWER build has keys this build does not know. Tolerance on read is what
        // makes additive-only safe; the keys are dropped, not fatal. The names here must be unknown at
        // EVERY level of the file, not just the one they sit at: until P4 this fixture used "SplitAxis"
        // and "Axis" as its stand-ins, and P4 made Axis a real key of SessionState — a test that spliced
        // a real key's name in somewhere and still passed would be passing for the wrong reason (a key
        // parsed, not an unknown one ignored), so no name below is a key anywhere in the format.
        string json = N(PreP3Literal
            .Replace("  \"ActiveId\": \"ses-1\",\n", "  \"ActiveId\": \"ses-1\",\n  \"Orientation\": \"stacked\",\n")
            .Replace("      \"Expanded\": true,\n", "      \"Expanded\": true,\n      \"Colour\": \"teal\",\n")
            .Replace("          \"Flagged\": true,\n", "          \"Flagged\": true,\n          \"Hud\": { \"a\": 1 },\n")
            .Replace("              \"Ratio\": 1,\n", "              \"Ratio\": 1,\n              \"Tilt\": 2,\n"));
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

    // ---- P4: the split axis (docs/plans/completed/2026-09-06-p4-splits.md, task 3) ----
    //
    // SessionState.Axis is the LAST key of a session, written only when the session is split AND
    // horizontal (RestoreState.StoreAxis), read back through RestoreState.LoadAxis, which accepts
    // exactly SplitAxes' two wire words and reads everything else — and absence — as vertical. So a
    // pre-P4 file loads vertical, a vertical split writes the bytes 0.17.12 wrote, and a hand-edited
    // spelling the verb would refuse cannot lay a session out.

    /// <summary>The fixture's session split in two — pane 0 keeps the session id (CreateSession mints it
    /// that way), pane 1 has its own — with the given STORED axis: null is what a save puts for a vertical
    /// split (StoreAxis), <c>"horizontal"</c> for a stacked one.</summary>
    private static AppState SplitTree(string? storedAxis)
    {
        var st = Tree();
        var s = st.Workspaces[0].Sessions[0];
        s.Panes[0].Ratio = 0.5f;
        s.Panes.Add(new PaneState { Id = "pane-2", Cwd = @"C:\Users\boris", FontSize = 16, Ratio = 0.5f, Command = "" });
        s.Active = 1;
        s.Axis = storedAxis;
        return st;
    }

    [Fact]
    public void RoundTrip_PreservesAxis()
    {
        string json = RestoreState.Serialize(SplitTree(SplitAxes.Horizontal));
        Assert.Contains("\"Axis\": \"horizontal\"", json);
        var st = Load(json);
        var s = st.Workspaces[0].Sessions[0];
        Assert.Equal(SplitAxes.Horizontal, s.Axis);
        Assert.Equal(SplitAxes.Horizontal, RestoreState.LoadAxis(s.Axis));
        // The panes came back as they went: both ids, in order, with their shares and the focus.
        Assert.Equal(new[] { "ses-1", "pane-2" }, s.Panes.Select(p => p.Id));
        Assert.Equal(new[] { 0.5f, 0.5f }, s.Panes.Select(p => p.Ratio));
        Assert.Equal(1, s.Active);
        // ...and a second save writes the same file: the key survives its own round trip.
        Assert.Equal(json, RestoreState.Serialize(st));
    }

    [Fact]
    public void SwappedSession_RoundTripsWithItsIdsInTheirNewOrder_AndNoDuplicate()
    {
        // P4's `session swap` puts the pane that carries the session id in slot 1 and leaves pane 0 with
        // its own id. The FORMAT has no opinion on which slot carries the session id — the ids are
        // written and read verbatim — and this pins that: the saved order comes back, both ids come
        // back, and the session id is on exactly one pane. (The app's loader then creates pane 0 under
        // its SAVED id rather than the session id — restore-roundtrip.ps1's swap-killed cell pins that.)
        var st = SplitTree(SplitAxes.Horizontal);
        var s0 = st.Workspaces[0].Sessions[0];
        s0.Panes.Reverse();                                     // what a swap leaves: [pane-2, ses-1]
        s0.Panes[0].Ratio = 0.7f; s0.Panes[1].Ratio = 0.3f;     // the sequence a swap keeps
        s0.Active = 0;
        string json = RestoreState.Serialize(st);

        var s = Load(json).Workspaces[0].Sessions[0];

        Assert.Equal("ses-1", s.Id);
        Assert.Equal(new[] { "pane-2", "ses-1" }, s.Panes.Select(p => p.Id));
        Assert.Distinct(s.Panes.Select(p => p.Id));
        Assert.Single(s.Panes, p => p.Id == s.Id);
        Assert.Equal(new[] { 0.7f, 0.3f }, s.Panes.Select(p => p.Ratio));
        Assert.Equal(0, s.Active);
        Assert.Equal(SplitAxes.Horizontal, RestoreState.LoadAxis(s.Axis));
        Assert.Equal(json, RestoreState.Serialize(Load(json)));
    }

    [Fact]
    public void Axis_IsTheLastKeyOfTheSession()
    {
        // Property order is the file's key order (RestoreState's header): a NEW key goes at the end of
        // its class, so every key a 0.17.12 file has keeps its position and a file with the key differs
        // from one without by exactly one inserted line. The regex pins "after BgMode, then the session
        // closes" — not just "somewhere after BgMode".
        string json = RestoreState.Serialize(SplitTree(SplitAxes.Horizontal));
        Assert.Matches("\"BgMode\": \"fit\",\r?\n\\s+\"Axis\": \"horizontal\"\r?\n\\s+\\}", json);
        Assert.Equal(1, System.Text.RegularExpressions.Regex.Matches(json, "\"Axis\"").Count);
    }

    [Fact]
    public void OldShapeFile_LoadsVertical()
    {
        // A 0.17.12 file has no Axis key (a pre-P4 build has no such property): the session is vertical —
        // the only layout that build had, and the only meaning "no key" can have.
        var st = Load(PreP3File);
        var s = st.Workspaces[0].Sessions[0];
        Assert.Null(s.Axis);
        Assert.Equal(SplitAxes.Vertical, RestoreState.LoadAxis(s.Axis));
        AssertEveryOtherFieldIntact(st);
    }

    [Fact]
    public void VerticalSession_WritesNoAxisKey()
    {
        // The write-back comparison, stated as such (the pre-P3-bytes twin): a P4 build saving a tree
        // without a horizontal split must write what 0.17.12 writes for that tree — the key is ABSENT,
        // not "Axis": null and not "Axis": "vertical". A single-pane session is the fixture's own bytes;
        // a vertical split is the fixture plus a pane and nothing else.
        Assert.Null(RestoreState.StoreAxis(paneCount: 1, axis: SplitAxes.Vertical));
        Assert.Null(RestoreState.StoreAxis(paneCount: 2, axis: SplitAxes.Vertical));
        Assert.Equal(PreP3File, RestoreState.Serialize(Tree()));
        string vertical = RestoreState.Serialize(SplitTree(RestoreState.StoreAxis(2, SplitAxes.Vertical)));
        Assert.DoesNotContain("Axis", vertical);
        Assert.Contains("\"Id\": \"pane-2\"", vertical);
        // A stacked split is exactly that file plus the one key.
        string horizontal = RestoreState.Serialize(SplitTree(RestoreState.StoreAxis(2, SplitAxes.Horizontal)));
        Assert.Equal(vertical, horizontal.Replace("," + Environment.NewLine + "          \"Axis\": \"horizontal\"", ""));
    }

    [Fact]
    public void StoreAxis_WritesTheKeyOnlyForASplitHorizontalSession()
    {
        // A single-pane session keeps its axis in memory for its next `split on` (Ses.Axis survives
        // `off`), but the file does not carry it: the key means "these two panes are stacked". So a
        // once-horizontal session collapsed to one pane saves the pre-P4 bytes, and a file with
        // "Axis": "horizontal" on a single-pane session (Task 7's edge case) is not written back.
        Assert.Equal(SplitAxes.Horizontal, RestoreState.StoreAxis(paneCount: 2, axis: SplitAxes.Horizontal));
        Assert.Null(RestoreState.StoreAxis(paneCount: 1, axis: SplitAxes.Horizontal));
        Assert.Null(RestoreState.StoreAxis(paneCount: 0, axis: SplitAxes.Horizontal));
    }

    [Theory]
    [InlineData("horizontal", "horizontal")]
    [InlineData("vertical", "vertical")]
    [InlineData("h", "vertical")]                        // the verb refuses the abbreviation; so does the file
    [InlineData("Horizontal", "vertical")]               // case-sensitive: the wire spelling, not a guess
    [InlineData("", "vertical")]
    [InlineData("diagonal", "vertical")]
    [InlineData(null, "vertical")]                       // absent = a pre-P4 file, or a vertical split (no key)
    public void LoadAxis_DropsWhatTheVerbWouldRefuse(string? stored, string expected)
    {
        Assert.Equal(expected, RestoreState.LoadAxis(stored));
        // The two rules are the same rule: whatever LoadAxis keeps, SplitAxes.TryParse accepts, and vice versa.
        bool verbAccepts = SplitAxes.TryParse(stored, out var parsed, out _) && parsed is not null;
        Assert.Equal(verbAccepts, stored is not null && RestoreState.LoadAxis(stored) == stored);
    }

    [Fact]
    public void OutOfRulesAxis_InAFile_SurvivesTheParseAndIsDroppedOnLoad()
    {
        // A hand-edited (or foreign) file with a word the verb would refuse: JSON parses it fine — the
        // POCO carries the raw value, so a round-trip does not launder it — and the loader lays the
        // session out vertical. Every other field is intact: a bad axis is not a bad file.
        string json = N(PreP3Literal.Replace("          \"BgMode\": \"fit\"\n", "          \"BgMode\": \"fit\",\n          \"Axis\": \"diagonal\"\n"));
        var st = Load(json);
        Assert.Equal("diagonal", st.Workspaces[0].Sessions[0].Axis);
        Assert.Equal(SplitAxes.Vertical, RestoreState.LoadAxis(st.Workspaces[0].Sessions[0].Axis));
        AssertEveryOtherFieldIntact(st);
    }

    [Fact]
    public void HorizontalKey_OnASinglePaneSession_LoadsButMeansNothing()
    {
        // The key on a one-pane session (a file edited by hand, or saved mid-way by a build that wrote it
        // differently): it parses and LoadAxis would accept the word, but the host applies an axis only to
        // a split session and StoreAxis never writes one for a single pane — so the next save drops it.
        string json = N(PreP3Literal.Replace("          \"BgMode\": \"fit\"\n", "          \"BgMode\": \"fit\",\n          \"Axis\": \"horizontal\"\n"));
        var st = Load(json);
        var s = st.Workspaces[0].Sessions[0];
        Assert.Single(s.Panes);
        Assert.Equal(SplitAxes.Horizontal, s.Axis);
        s.Axis = RestoreState.StoreAxis(s.Panes.Count, RestoreState.LoadAxis(s.Axis));   // what the host's next save computes
        Assert.Equal(PreP3File, RestoreState.Serialize(st));
        AssertEveryOtherFieldIntact(st);
    }
}
