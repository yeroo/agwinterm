using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace Agwinterm.Win32;

// UIA Text pattern (T2-14 Stage 2): lets Narrator/NVDA read and navigate the terminal by
// character / word / line, track the caret, and box it — the "proper" screen-reader support.
// The document is the active pane's recent scrollback + live screen flattened into one string
// (lines joined by '\n'); a text range is a [start,end) character span into it. Ranges report
// on-screen bounding rectangles for the visible portion so the caret box lands where you type.

partial class Uia
{
    /// <summary>Snapshot of the active pane's text buffer for the text pattern (built by the app under
    /// the session lock; see Program.BuildUiaTextSnapshot).</summary>
    internal sealed class TextSnapshot
    {
        public required string[] Lines;
        public required int[] LineStart;      // LineStart[i] = char offset of Lines[i]; LineStart[n] = total+1
        public int TotalLength;
        public int CaretOffset;
        public int FirstVisibleLine, VisibleRows;
        public double ScreenX, ScreenY, CellW, CellH;   // top-left of the on-screen grid (screen px) + cell size

        public string Text => string.Join('\n', Lines);
        public (int line, int col) LineColOf(int off)
        {
            off = Math.Clamp(off, 0, TotalLength);
            // binary search the line whose [start, start+len] contains off
            int lo = 0, hi = Lines.Length - 1, line = 0;
            while (lo <= hi)
            {
                int mid = (lo + hi) / 2;
                if (off < LineStart[mid]) hi = mid - 1;
                else if (off > LineStart[mid] + Lines[mid].Length) lo = mid + 1;
                else { line = mid; break; }
            }
            if (lo > hi) line = Math.Clamp(lo, 0, Lines.Length - 1);
            return (line, off - LineStart[line]);
        }
    }

    internal static Func<TextSnapshot?>? GetTextSnapshot;
    internal static TextSnapshot Snap() => GetTextSnapshot?.Invoke() ?? EmptySnap;
    private static readonly TextSnapshot EmptySnap = new() { Lines = new[] { "" }, LineStart = new[] { 0, 1 } };

    internal static readonly Guid IID_ITextProvider = new("3589c92c-63f3-4367-99bb-ada653b77cf2");
    internal static readonly Guid IID_ITextRangeProvider = new("5347ad7b-c355-46f8-aff5-909033582f63");

    /// <summary>CCW for a managed COM object, QI'd to <paramref name="iid"/> (AddRef'd; caller releases). 0 on failure.</summary>
    internal static nint AsInterface(object obj, in Guid iid)
    {
        nint unk = ComWrappers.GetOrCreateComInterfaceForObject(obj, CreateComInterfaceFlags.None);
        try { Guid id = iid; return Marshal.QueryInterface(unk, in id, out nint p) >= 0 ? p : 0; }
        finally { Marshal.Release(unk); }
    }

    /// <summary>Notify listeners that the terminal text changed (so a reader re-reads new output).</summary>
    internal static void RaiseTextChanged()
    {
        if (_providerSimple == 0) return;
        try { UiaRaiseAutomationEvent(_providerSimple, UIA_Text_TextChangedEventId); } catch { }
    }

    private const int UIA_Text_TextChangedEventId = 20015;
    [LibraryImport("uiautomationcore.dll")] private static partial int UiaRaiseAutomationEvent(nint provider, int eventId);
}

internal enum TextUnit { Character = 0, Format = 1, Word = 2, Line = 3, Paragraph = 4, Page = 5, Document = 6 }
internal enum TextPatternRangeEndpoint { Start = 0, End = 1 }
internal enum SupportedTextSelection { None = 0, Single = 1, Multiple = 2 }

[GeneratedComInterface]
[Guid("3589c92c-63f3-4367-99bb-ada653b77cf2")]
internal partial interface ITextProvider
{
    nint GetSelection();                                    // SAFEARRAY(ITextRangeProvider*)
    nint GetVisibleRanges();                                // SAFEARRAY(ITextRangeProvider*)
    nint RangeFromChild(nint childElement);                 // ITextRangeProvider*
    nint RangeFromPoint(UiaPoint point);                    // ITextRangeProvider*
    nint GetDocumentRange();                                // ITextRangeProvider*  (get_DocumentRange)
    SupportedTextSelection GetSupportedTextSelection();     // get_SupportedTextSelection
}

[StructLayout(LayoutKind.Sequential)]
internal struct UiaPoint { public double X, Y; }

[GeneratedComInterface]
[Guid("5347ad7b-c355-46f8-aff5-909033582f63")]
internal partial interface ITextRangeProvider
{
    nint Clone();
    [return: MarshalAs(UnmanagedType.I4)] int Compare(ITextRangeProvider range);              // BOOL
    int CompareEndpoints(TextPatternRangeEndpoint endpoint, ITextRangeProvider targetRange, TextPatternRangeEndpoint targetEndpoint);
    void ExpandToEnclosingUnit(TextUnit unit);
    nint FindAttribute(int attributeId, VariantBlob val, [MarshalAs(UnmanagedType.I4)] int backward);  // ITextRangeProvider*
    nint FindText([MarshalUsing(typeof(BStrStringMarshaller))] string text, [MarshalAs(UnmanagedType.I4)] int backward, [MarshalAs(UnmanagedType.I4)] int ignoreCase);
    void GetAttributeValue(int attributeId, nint pRetVal);   // VARIANT* (caller-allocated)
    nint GetBoundingRectangles();                            // SAFEARRAY(double)
    nint GetEnclosingElement();                              // IRawElementProviderSimple*
    [return: MarshalUsing(typeof(BStrStringMarshaller))] string GetText(int maxLength);
    int Move(TextUnit unit, int count);
    int MoveEndpointByUnit(TextPatternRangeEndpoint endpoint, TextUnit unit, int count);
    void MoveEndpointByRange(TextPatternRangeEndpoint endpoint, ITextRangeProvider targetRange, TextPatternRangeEndpoint targetEndpoint);
    void Select();
    void AddToSelection();
    void RemoveFromSelection();
    void ScrollIntoView([MarshalAs(UnmanagedType.I4)] int alignToTop);
    nint GetChildren();                                      // SAFEARRAY(ITextRangeProvider*)
}

/// <summary>24-byte blittable stand-in for a by-value VARIANT (FindAttribute's [in] val) — we never
/// read it, but the parameter must occupy the right stack space so the vtable slot stays aligned.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct VariantBlob { public long A, B, C; }

// UiaTerminal (declared in Uia.Tree.cs) also implements ITextProvider — the terminal Document element.
internal partial class UiaTerminal : ITextProvider
{
    public nint GetSelection()
    {
        var s = Uia.Snap();
        // A degenerate range at the caret — Narrator announces the caret line.
        return UiaArrays.RangeArray(new[] { new UiaTextRange(s.CaretOffset, s.CaretOffset) });
    }

    public nint GetVisibleRanges()
    {
        var s = Uia.Snap();
        int firstOff = s.LineStart[Math.Clamp(s.FirstVisibleLine, 0, s.Lines.Length - 1)];
        int lastLine = Math.Clamp(s.FirstVisibleLine + s.VisibleRows - 1, 0, s.Lines.Length - 1);
        int lastOff = s.LineStart[lastLine] + s.Lines[lastLine].Length;
        return UiaArrays.RangeArray(new[] { new UiaTextRange(firstOff, lastOff) });
    }

    public nint RangeFromChild(nint childElement) => Uia.AsInterface(new UiaTextRange(0, 0), Uia.IID_ITextRangeProvider);

    public nint RangeFromPoint(UiaPoint point)
    {
        var s = Uia.Snap();
        int row = (int)((point.Y - s.ScreenY) / Math.Max(1, s.CellH));
        int col = (int)((point.X - s.ScreenX) / Math.Max(1, s.CellW));
        int line = Math.Clamp(s.FirstVisibleLine + Math.Max(0, row), 0, s.Lines.Length - 1);
        int off = s.LineStart[line] + Math.Clamp(col, 0, s.Lines[line].Length);
        return Uia.AsInterface(new UiaTextRange(off, off), Uia.IID_ITextRangeProvider);
    }

    public nint GetDocumentRange() => Uia.AsInterface(new UiaTextRange(0, Uia.Snap().TotalLength), Uia.IID_ITextRangeProvider);

    public SupportedTextSelection GetSupportedTextSelection() => SupportedTextSelection.Single;
}

/// <summary>A [Start,End) character span into the flattened terminal document. Reads a fresh snapshot on
/// each call so it tracks live output. Immutable offsets except where a Move/Expand mutates them.</summary>
[GeneratedComClass]
internal partial class UiaTextRange : ITextRangeProvider
{
    private int _start, _end;
    public UiaTextRange(int start, int end) { _start = Math.Min(start, end); _end = Math.Max(start, end); }

    private static int ClampOff(int off) { var s = Uia.Snap(); return Math.Clamp(off, 0, s.TotalLength); }

    public nint Clone() => Uia.AsInterface(new UiaTextRange(_start, _end), Uia.IID_ITextRangeProvider);

    public int Compare(ITextRangeProvider range) => range is UiaTextRange r && r._start == _start && r._end == _end ? 1 : 0;

    public int CompareEndpoints(TextPatternRangeEndpoint endpoint, ITextRangeProvider targetRange, TextPatternRangeEndpoint targetEndpoint)
    {
        int a = endpoint == TextPatternRangeEndpoint.Start ? _start : _end;
        int b = targetRange is UiaTextRange r ? (targetEndpoint == TextPatternRangeEndpoint.Start ? r._start : r._end) : 0;
        return a.CompareTo(b);
    }

    public void ExpandToEnclosingUnit(TextUnit unit)
    {
        var s = Uia.Snap();
        switch (unit)
        {
            case TextUnit.Character:
                _end = Math.Min(_start + 1, s.TotalLength);
                if (_end == _start && _start > 0) _start--;
                break;
            case TextUnit.Word:
            {
                var (ln, col) = s.LineColOf(_start);
                string text = s.Lines[ln];
                int ws = col, we = col;
                while (ws > 0 && !char.IsWhiteSpace(text[ws - 1])) ws--;
                while (we < text.Length && !char.IsWhiteSpace(text[we])) we++;
                _start = s.LineStart[ln] + ws; _end = s.LineStart[ln] + we;
                break;
            }
            case TextUnit.Document:
                _start = 0; _end = s.TotalLength;
                break;
            default: // Line / Paragraph / Page → the whole line
            {
                var (ln, _) = s.LineColOf(_start);
                _start = s.LineStart[ln];
                _end = s.LineStart[ln] + s.Lines[ln].Length;
                break;
            }
        }
    }

    public nint FindAttribute(int attributeId, VariantBlob val, int backward) => 0;   // not supported (null range)

    public nint FindText(string text, int backward, int ignoreCase)
    {
        var s = Uia.Snap();
        string hay = s.Text;
        int from = Math.Clamp(_start, 0, hay.Length), to = Math.Clamp(_end, 0, hay.Length);
        string window = hay.Substring(from, Math.Max(0, to - from));
        var cmp = ignoreCase != 0 ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        int idx = backward != 0 ? window.LastIndexOf(text ?? "", cmp) : window.IndexOf(text ?? "", cmp);
        if (idx < 0 || string.IsNullOrEmpty(text)) return 0;
        return Uia.AsInterface(new UiaTextRange(from + idx, from + idx + text.Length), Uia.IID_ITextRangeProvider);
    }

    public void GetAttributeValue(int attributeId, nint pRetVal)
    {
        // VT_UNKNOWN = the "not supported" sentinel (UiaGetReservedNotSupportedValue); we just leave
        // VT_EMPTY, which UIA treats as unsupported. Caller allocated the VARIANT; init it to empty.
        VariantInit(pRetVal);
    }

    public nint GetBoundingRectangles()
    {
        var s = Uia.Snap();
        var rects = new List<double>();
        var (l0, c0) = s.LineColOf(_start);
        var (l1, c1) = s.LineColOf(_end);
        for (int ln = l0; ln <= l1; ln++)
        {
            int screenRow = ln - s.FirstVisibleLine;
            if (screenRow < 0 || screenRow >= s.VisibleRows) continue;   // off-screen (scrolled away)
            int cs = ln == l0 ? c0 : 0;
            int ce = ln == l1 ? c1 : s.Lines[ln].Length;
            if (ce < cs) ce = cs;
            double x = s.ScreenX + cs * s.CellW;
            double y = s.ScreenY + screenRow * s.CellH;
            double w = Math.Max(ce == cs ? 1 : (ce - cs) * s.CellW, 1);   // caret (degenerate) → 1px sliver
            rects.Add(x); rects.Add(y); rects.Add(w); rects.Add(s.CellH);
        }
        return UiaArrays.DoubleArray(rects.ToArray());
    }

    public nint GetEnclosingElement() => Uia.RootProvider();

    public string GetText(int maxLength)
    {
        var s = Uia.Snap();
        string hay = s.Text;
        int from = Math.Clamp(_start, 0, hay.Length), to = Math.Clamp(_end, 0, hay.Length);
        string t = hay.Substring(from, Math.Max(0, to - from));
        return maxLength >= 0 && t.Length > maxLength ? t.Substring(0, maxLength) : t;
    }

    public int Move(TextUnit unit, int count)
    {
        // Move the whole (collapsed-to-start) range by `count` units; return units actually moved.
        var s = Uia.Snap();
        if (unit == TextUnit.Character)
        {
            int target = Math.Clamp(_start + count, 0, s.TotalLength);
            int moved = target - _start;
            _start = _end = target;
            return moved;
        }
        // Line (and coarser): step whole lines.
        var (ln, _) = s.LineColOf(_start);
        int newLn = Math.Clamp(ln + count, 0, s.Lines.Length - 1);
        _start = _end = s.LineStart[newLn];
        // collapse then re-expand to the line for a sensible caret
        _end = s.LineStart[newLn] + s.Lines[newLn].Length;
        return newLn - ln;
    }

    public int MoveEndpointByUnit(TextPatternRangeEndpoint endpoint, TextUnit unit, int count)
    {
        var s = Uia.Snap();
        int cur = endpoint == TextPatternRangeEndpoint.Start ? _start : _end;
        int target;
        if (unit == TextUnit.Character) target = Math.Clamp(cur + count, 0, s.TotalLength);
        else
        {
            var (ln, _) = s.LineColOf(cur);
            int newLn = Math.Clamp(ln + count, 0, s.Lines.Length - 1);
            target = count >= 0 ? s.LineStart[newLn] + s.Lines[newLn].Length : s.LineStart[newLn];
        }
        int moved = target - cur;
        if (endpoint == TextPatternRangeEndpoint.Start) { _start = target; if (_end < _start) _end = _start; }
        else { _end = target; if (_start > _end) _start = _end; }
        return unit == TextUnit.Character ? moved : Math.Sign(count) * Math.Abs(count);
    }

    public void MoveEndpointByRange(TextPatternRangeEndpoint endpoint, ITextRangeProvider targetRange, TextPatternRangeEndpoint targetEndpoint)
    {
        if (targetRange is not UiaTextRange r) return;
        int v = targetEndpoint == TextPatternRangeEndpoint.Start ? r._start : r._end;
        if (endpoint == TextPatternRangeEndpoint.Start) { _start = v; if (_end < _start) _end = _start; }
        else { _end = v; if (_start > _end) _start = _end; }
    }

    public void Select() { }              // read-only selection model for now
    public void AddToSelection() { }
    public void RemoveFromSelection() { }
    public void ScrollIntoView(int alignToTop) { }

    public nint GetChildren() => UiaArrays.RangeArray(Array.Empty<UiaTextRange>());

    [LibraryImport("oleaut32.dll")] private static partial void VariantInit(nint pvarg);
}

/// <summary>SAFEARRAY builders for the text-pattern out-params (source-gen COM returns them as raw pointers).</summary>
internal static partial class UiaArrays
{
    private const ushort VT_R8 = 5, VT_UNKNOWN = 13;

    public static nint DoubleArray(double[] vals)
    {
        nint sa = SafeArrayCreateVector(VT_R8, 0, (uint)vals.Length);
        if (sa != 0 && vals.Length > 0 && SafeArrayAccessData(sa, out nint data) >= 0)
        {
            Marshal.Copy(vals, 0, data, vals.Length);
            SafeArrayUnaccessData(sa);
        }
        return sa;
    }

    public static nint RangeArray(UiaTextRange[] ranges)
    {
        nint sa = SafeArrayCreateVector(VT_UNKNOWN, 0, (uint)ranges.Length);
        if (sa == 0) return 0;
        for (int i = 0; i < ranges.Length; i++)
        {
            nint p = Uia.AsInterface(ranges[i], Uia.IID_ITextRangeProvider);
            if (p == 0) continue;
            int idx = i;
            SafeArrayPutElement(sa, ref idx, p);   // AddRefs for VT_UNKNOWN
            Marshal.Release(p);
        }
        return sa;
    }

    [LibraryImport("oleaut32.dll")] private static partial nint SafeArrayCreateVector(ushort vt, int lLbound, uint cElements);
    [LibraryImport("oleaut32.dll")] private static partial int SafeArrayAccessData(nint psa, out nint ppvData);
    [LibraryImport("oleaut32.dll")] private static partial int SafeArrayUnaccessData(nint psa);
    [LibraryImport("oleaut32.dll")] private static partial int SafeArrayPutElement(nint psa, ref int rgIndices, nint pv);
}
