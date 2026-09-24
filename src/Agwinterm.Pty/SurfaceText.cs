using System.Text;
using System.Text.Json;
using Agwinterm.Core;

namespace Agwinterm.Pty;

/// <summary>
/// The ONE reader behind <c>session text</c> and <c>session overlay text</c> (P5): a surface's buffer
/// as plain text (or attribute runs for <c>session text --styles</c>), trailing blank lines trimmed.
/// Exposed here rather than kept private to the server
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
            // Absolute numbering: [0, hist) is scrollback, then the live rows.
            for (int abs = FirstRow(rows, hist, args); abs < hist + rows; abs++)
                sb.Append(abs < hist ? em.DumpHistoryRow(abs) : em.DumpRow(abs - hist)).Append('\n');
        }
        return sb.ToString().TrimEnd('\n');
    }

    private static int FirstRow(int rows, int hist, OverlayTextArgs args)
    {
        int take = args.All ? rows + hist : args.Lines <= 0 ? rows : Math.Min(args.Lines, rows + hist);
        return hist + rows - take;
    }

    private static readonly JsonSerializerOptions StyledJson = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private sealed record StyledRow(int Row, List<StyledRun> Runs);
    private sealed record StyledRun(int Col, int Width, string Text, bool Faint, bool Bold,
        bool Italic, bool Underline, bool Inverse, bool Strike, string Fg, string Bg);

    /// <summary>JSON text runs and cursor from one locked snapshot. Text trims exactly like
    /// <see cref="Dump"/> (including styled trailing whitespace); run extents use grid columns,
    /// not UTF-16 or codepoint counts. Row zero is the screen top; negative rows are history.</summary>
    public static string DumpStyled(ISession s, OverlayTextArgs args)
    {
        lock (s.SyncRoot)
        {
            var em = s.Emulator;
            int rows = em.Screen.Rows, cols = em.Screen.Cols, hist = em.HistoryCount;
            var result = new List<StyledRow>();
            for (int abs = FirstRow(rows, hist, args); abs < hist + rows; abs++)
            {
                Cell At(int col) => abs < hist ? em.GetHistoryCell(abs, col) : em.Screen[abs - hist, col];
                // DumpRow uses string.TrimEnd(): all Unicode whitespace, regardless of style.
                // No supplementary-plane codepoints are whitespace in .NET's TrimEnd set.
                int last = cols - 1;
                while (last >= 0)
                {
                    var cell = At(last);
                    if (cell.Width != 0 && !(cell.Rune <= char.MaxValue && char.IsWhiteSpace((char)cell.Rune))) break;
                    last--;
                }

                var runs = new List<StyledRun>();
                var text = new StringBuilder();
                Cell style = default;
                int start = 0, end = 0;
                void Flush()
                {
                    if (text.Length == 0) return;
                    var a = style.Attributes;
                    runs.Add(new(start, end - start, text.ToString(),
                        (a & CellAttributes.Dim) != 0, (a & CellAttributes.Bold) != 0,
                        (a & CellAttributes.Italic) != 0, (a & CellAttributes.Underline) != 0,
                        (a & CellAttributes.Inverse) != 0, (a & CellAttributes.Strikethrough) != 0,
                        Spec(style.FgSpec), Spec(style.BgSpec)));
                    text.Clear();
                }
                for (int c = 0; c <= last; c++)
                {
                    var cell = At(c);
                    if (cell.Width == 0) continue;
                    if (text.Length > 0 && (style.Attributes != cell.Attributes ||
                        style.FgSpec != cell.FgSpec || style.BgSpec != cell.BgSpec)) Flush();
                    if (text.Length == 0) { start = c; style = cell; }
                    if (cell.Rune > char.MaxValue) text.Append(char.ConvertFromUtf32(cell.Rune));
                    else text.Append((char)cell.Rune);
                    end = Math.Min(cols, c + cell.Width);
                }
                Flush();
                result.Add(new(abs - hist, runs));
            }
            while (result.Count > 0 && result[^1].Runs.Count == 0) result.RemoveAt(result.Count - 1);
            return JsonSerializer.Serialize(new { cols, rows = result,
                cursor = new { row = em.CursorRow, col = em.CursorCol } }, StyledJson);
        }
    }

    private static string Spec(ColorSpec spec) => spec.Kind switch
    {
        ColorSpecKind.Indexed => $"idx:{spec.Index}",
        ColorSpecKind.Rgb => $"#{spec.Rgb.R:x2}{spec.Rgb.G:x2}{spec.Rgb.B:x2}",
        _ => "default",
    };
}
