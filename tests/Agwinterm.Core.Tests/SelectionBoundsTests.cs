using System.Reflection;

namespace Agwinterm.Core.Tests;

/// <summary>
/// Guards the one bound the whole selection invariant now rests on.
///
/// A selection may only name lines the pane can SHOW, because the highlight and the clipboard read
/// the same absolute rows: a selection outside that range is painted nowhere yet still copies,
/// handing over text the user never saw highlighted. On the main screen the range starts at line 0.
/// On the ALT screen it starts at HistoryCount — the main screen's scrollback is still in the
/// buffer, but it belongs to the other screen and the renderer never draws it there.
///
/// That rule lives in one place, Program.ClampSel, which every reconcile ends in. It was reached
/// three times by three different doors (Select All anchoring at 0, mark-mode Up flooring at 0,
/// drag-autoscroll walking the focus down through a scroll offset), and each fix reopened it
/// somewhere else. Nothing in the repo failed when it regressed, hence this.
///
/// Reflection over the built UI assembly, following PInvokeCharSetTests: Program is internal and
/// ClampSel is private static, but it is pure over ints and a Pane field bag.
/// </summary>
public class SelectionBoundsTests
{
    private static string FindUiAssembly()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !Directory.Exists(Path.Combine(dir, "src", "Agwinterm.Win32")))
            dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        var bin = Path.Combine(dir!, "src", "Agwinterm.Win32", "bin");
        Assert.True(Directory.Exists(bin), $"build Agwinterm.Win32 first (no {bin})");
        var hits = Directory.GetFiles(bin, "Agwinterm.Win32.dll", SearchOption.AllDirectories)
                            .OrderByDescending(File.GetLastWriteTimeUtc).ToArray();
        Assert.True(hits.Length > 0, $"build Agwinterm.Win32 first (no dll under {bin})");
        return hits[0];
    }

    private static readonly Assembly Ui = Assembly.LoadFrom(FindUiAssembly());

    private static Type PaneType => Ui.GetTypes().First(t => t.Name == "Pane");

    private static MethodInfo Clamp => Ui.GetTypes().First(t => t.Name == "Program")
        .GetMethod("ClampSel", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("Program.ClampSel is gone — the selection bound moved, so this test must follow it");

    /// <summary>A Pane without running its constructor: ClampSel touches only the Sel* fields.</summary>
    private static object NewPane(int ancLine, int ancCol, int focLine, int focCol, bool block = false)
    {
        object p = System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(PaneType);
        void Set(string f, object v) => PaneType.GetField(f)!.SetValue(p, v);
        Set("SelAncLine", ancLine); Set("SelAncCol", ancCol);
        Set("SelFocLine", focLine); Set("SelFocCol", focCol);
        Set("HasSel", true); Set("BlockSel", block);
        return p;
    }

    private static int Get(object pane, string field) => (int)PaneType.GetField(field)!.GetValue(pane)!;

    private static bool Run(object pane, int hist, int rows, int cols, bool alt)
        => (bool)Clamp.Invoke(null, new object[] { pane, hist, rows, cols, alt })!;

    [Fact]
    public void AltScreen_SelectionCannotReachTheOtherScreensHistory()
    {
        // What Select All builds: line 0 through the last live row. On the alt screen every line
        // below `hist` is main-screen scrollback the renderer will not highlight.
        var p = NewPane(0, 0, 529, 79);
        Assert.True(Run(p, hist: 500, rows: 30, cols: 80, alt: true));
        Assert.Equal(500, Get(p, "SelAncLine"));   // pulled up to the first line the alt screen shows
        Assert.Equal(529, Get(p, "SelFocLine"));
    }

    [Fact]
    public void AltScreen_SelectionEntirelyInHistoryIsDropped()
    {
        // Mark-mode Up and drag-autoscroll used to walk the focus down here; nothing highlights it,
        // so it must die rather than be pinned to the top row.
        var p = NewPane(480, 0, 495, 79);
        Assert.False(Run(p, hist: 500, rows: 30, cols: 80, alt: true));
        Assert.False((bool)PaneType.GetField("HasSel")!.GetValue(p)!);
    }

    [Fact]
    public void MainScreen_DeepScrollbackSelectionIsUntouched()
    {
        // The same call with alt: false must be a no-op — history IS shown on the main screen.
        var p = NewPane(12, 3, 34, 40);
        Assert.True(Run(p, hist: 500, rows: 30, cols: 80, alt: false));
        Assert.Equal(12, Get(p, "SelAncLine"));
        Assert.Equal(34, Get(p, "SelFocLine"));
        Assert.Equal(3, Get(p, "SelAncCol"));
        Assert.Equal(40, Get(p, "SelFocCol"));
    }

    [Fact]
    public void SelectionPastTheEndIsDropped_NotPinnedToTheLastRow()
    {
        // Resize shrinks the screen without reflowing, so a selection on the bottom rows can end up
        // past the end of the buffer, where SelectionText reads blank cells.
        var p = NewPane(540, 0, 545, 79);
        Assert.False(Run(p, hist: 500, rows: 30, cols: 80, alt: false));
    }

    [Fact]
    public void BlockSelectionRightOfANarrowerScreenIsDropped()
    {
        // Clamping both columns would move the rectangle onto an unrelated stripe.
        var p = NewPane(10, 100, 14, 110, block: true);
        Assert.False(Run(p, hist: 0, rows: 30, cols: 80, alt: false));
    }

    [Fact]
    public void ColumnsAreClampedWithinTheScreen()
    {
        var p = NewPane(2, 0, 6, 200);
        Assert.True(Run(p, hist: 0, rows: 30, cols: 80, alt: false));
        Assert.Equal(79, Get(p, "SelFocCol"));
    }
}
