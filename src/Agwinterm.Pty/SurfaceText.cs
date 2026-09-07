using System.Text;

namespace Agwinterm.Pty;

/// <summary>
/// The ONE reader behind <c>session text</c> and <c>session overlay text</c> (P5): a surface's buffer
/// as plain text, trailing blank lines trimmed. Exposed here rather than kept private to the server
/// because the app host reads an overlay's terminal with it too — a second walk would be a second
/// answer to "what is on this surface".
/// </summary>
public static class SurfaceText
{
    /// <summary>
    /// Dump <paramref name="s"/>'s buffer. <see cref="OverlayTextArgs.All"/>: every history row and
    /// then the screen. Else <see cref="OverlayTextArgs.Lines"/> reaches back into scrollback — the
    /// last N lines ending at the bottom of the visible screen, so an N larger than the screen height
    /// picks up history; 0 keeps the old meaning exactly: the visible screen.
    ///
    /// Scrollback matters because the interesting part is usually already gone: a launch banner, a
    /// version, an error printed before a full-screen app took the alt screen. An agent reading a
    /// pane it did not watch could reach none of it.
    /// </summary>
    public static string Dump(ISession s, OverlayTextArgs args)
    {
        var sb = new StringBuilder();
        lock (s.SyncRoot)
        {
            var em = s.Emulator;
            int rows = em.Screen.Rows, hist = em.HistoryCount;
            int take = args.All ? rows + hist : args.Lines <= 0 ? rows : Math.Min(args.Lines, rows + hist);
            // Absolute numbering: [0, hist) is scrollback, then the live rows.
            for (int abs = hist + rows - take; abs < hist + rows; abs++)
                sb.Append(abs < hist ? em.DumpHistoryRow(abs) : em.DumpRow(abs - hist)).Append('\n');
        }
        return sb.ToString().TrimEnd('\n');
    }
}
