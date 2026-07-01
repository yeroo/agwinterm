namespace Agwinterm.Core;

public sealed class TerminalEmulator : IParserPerformer
{
    private readonly VtParser _parser;
    private readonly ScreenBuffer _main;
    private readonly ScreenBuffer _alt;
    private ScreenBuffer _active;

    private int _savedRow;
    private int _savedCol;

    /// <summary>The active screen buffer (main or alternate).</summary>
    public ScreenBuffer Screen => _active;

    /// <summary>True when the alternate screen buffer is active.</summary>
    public bool IsAltScreen => ReferenceEquals(_active, _alt);

    public int CursorRow { get; private set; }
    public int CursorCol { get; private set; }

    /// <summary>Cursor visibility (DECTCEM, CSI ?25 h/l). Renderers should honor this.</summary>
    public bool CursorVisible { get; private set; } = true;

    private bool _mouseClick;   // ?1000 button press/release
    private bool _mouseDrag;    // ?1002 button-event (motion while pressed)
    private bool _mouseMotion;  // ?1003 any-motion
    private bool _mouseSgr;     // ?1006 SGR extended encoding

    /// <summary>True if the app requested mouse reporting (any of ?1000/?1002/?1003).</summary>
    public bool MouseReporting => _mouseClick || _mouseDrag || _mouseMotion;

    /// <summary>True if motion events (not just press/release) should be reported.</summary>
    public bool MouseReportsMotion => _mouseDrag || _mouseMotion;

    /// <summary>True if the app requested SGR (?1006) mouse encoding.</summary>
    public bool MouseSgr => _mouseSgr;

    private int _scrollTop;
    private int _scrollBottom;

    private Color _fg = Color.DefaultForeground;
    private Color _bg = Color.DefaultBackground;
    private CellAttributes _attrs = CellAttributes.None;

    /// <summary>Window title set via OSC 0/2.</summary>
    public string Title { get; private set; } = string.Empty;

    /// <summary>Working directory reported via OSC 7 (raw text, typically a file:// URI).</summary>
    public string Cwd { get; private set; } = string.Empty;

    public TerminalEmulator(int cols, int rows)
    {
        _main = new ScreenBuffer(cols, rows);
        _alt = new ScreenBuffer(cols, rows);
        _active = _main;
        _scrollTop = 0;
        _scrollBottom = rows - 1;
        _parser = new VtParser(this);
    }

    public void Feed(ReadOnlySpan<byte> bytes) => _parser.Feed(bytes);

    /// <summary>Resize both screen buffers, reset the scroll region, and clamp the cursor.</summary>
    public void Resize(int cols, int rows)
    {
        if (cols <= 0 || rows <= 0) return;
        _main.Resize(cols, rows);
        _alt.Resize(cols, rows);
        _scrollTop = 0;
        _scrollBottom = rows - 1;
        if (CursorRow >= rows) CursorRow = rows - 1;
        if (CursorCol >= cols) CursorCol = cols - 1;
    }

    public void Print(char ch)
    {
        int w = Wcwidth.Of(ch);
        if (w == 0) return; // combining/zero-width: dropped in v1 (see spec non-goals)

        if (CursorCol >= Screen.Cols)
        {
            CursorCol = 0;
            Index();
        }
        // A double-width glyph cannot straddle the right edge: wrap first.
        if (w == 2 && CursorCol == Screen.Cols - 1)
        {
            Screen[CursorRow, CursorCol] = Blank();
            CursorCol = 0;
            Index();
        }

        Screen[CursorRow, CursorCol] = new Cell(ch, _fg, _bg, _attrs, (byte)w);
        if (w == 2)
            Screen[CursorRow, CursorCol + 1] = new Cell('\0', _fg, _bg, _attrs, 0); // trailing spacer
        CursorCol += w;
    }

    public void Execute(byte control)
    {
        switch (control)
        {
            case 13: CursorCol = 0; break;                       // CR
            case 10: Index(); break;                             // LF
            case 8: if (CursorCol > 0) CursorCol--; break;       // BS
            case 9:                                              // HT
                CursorCol = Math.Min(Screen.Cols - 1, ((CursorCol / 8) + 1) * 8);
                break;
        }
    }

    public void CsiDispatch(char final, IReadOnlyList<int> parameters, char prefix)
    {
        int P(int index, int def) =>
            index < parameters.Count && parameters[index] != 0 ? parameters[index] : def;

        if (prefix == '?')
        {
            if (final is 'h' or 'l')
                SetPrivateMode(parameters, set: final == 'h');
            return;
        }

        switch (final)
        {
            case 'H': // CUP
            case 'f':
                CursorRow = Math.Clamp(P(0, 1) - 1, 0, Screen.Rows - 1);
                CursorCol = Math.Clamp(P(1, 1) - 1, 0, Screen.Cols - 1);
                break;
            case 'A': CursorRow = Math.Max(0, CursorRow - P(0, 1)); break;
            case 'B': CursorRow = Math.Min(Screen.Rows - 1, CursorRow + P(0, 1)); break;
            case 'C': CursorCol = Math.Min(Screen.Cols - 1, CursorCol + P(0, 1)); break;
            case 'D': CursorCol = Math.Max(0, CursorCol - P(0, 1)); break;
            case 'G': CursorCol = Math.Clamp(P(0, 1) - 1, 0, Screen.Cols - 1); break;
            case 'd': CursorRow = Math.Clamp(P(0, 1) - 1, 0, Screen.Rows - 1); break; // VPA
            case 'J': EraseDisplay(parameters.Count > 0 ? parameters[0] : 0); break;
            case 'K': EraseLine(parameters.Count > 0 ? parameters[0] : 0); break;
            case 'X': EraseChars(P(0, 1)); break; // ECH: blank N cells from cursor (cursor unmoved)
            case 'm': ApplySgr(parameters); break;
            case 'r': // DECSTBM scroll region
                SetScrollRegion(P(0, 1) - 1, parameters.Count > 1 && parameters[1] != 0 ? parameters[1] - 1 : Screen.Rows - 1);
                break;
            case 'L': InsertLines(P(0, 1)); break;   // IL
            case 'M': DeleteLines(P(0, 1)); break;   // DL
            case '@': InsertChars(P(0, 1)); break;   // ICH
            case 'P': DeleteChars(P(0, 1)); break;   // DCH
            case 'S': for (int i = 0; i < P(0, 1); i++) ScrollRegionUp(); break;   // SU
            case 'T': for (int i = 0; i < P(0, 1); i++) ScrollRegionDown(); break; // SD
        }
    }

    public void EscDispatch(char final)
    {
        switch (final)
        {
            case '7': SaveCursor(); break;       // DECSC
            case '8': RestoreCursor(); break;    // DECRC
            case 'D': Index(); break;            // IND
            case 'M': ReverseIndex(); break;     // RI
            case 'E': CursorCol = 0; Index(); break; // NEL
        }
    }

    private void SetPrivateMode(IReadOnlyList<int> parameters, bool set)
    {
        foreach (int mode in parameters)
        {
            switch (mode)
            {
                case 1049: // alt screen + save/restore cursor
                    if (set) { SaveCursor(); EnterAltScreen(); }
                    else { LeaveAltScreen(); RestoreCursor(); }
                    break;
                case 47:
                case 1047: // alt screen, no cursor save
                    if (set) EnterAltScreen();
                    else LeaveAltScreen();
                    break;
                case 25: // DECTCEM cursor visibility
                    CursorVisible = set;
                    break;
                case 1000: _mouseClick = set; break;   // mouse press/release
                case 1002: _mouseDrag = set; break;    // button-event (drag) tracking
                case 1003: _mouseMotion = set; break;  // any-motion tracking
                case 1006: _mouseSgr = set; break;     // SGR extended mouse encoding
            }
        }
    }

    private void EnterAltScreen()
    {
        if (IsAltScreen) return;
        _active = _alt;
        _active.Clear();
        _placements.Clear(); // images belong to a screen; start the alt screen clean
        CursorRow = 0;
        CursorCol = 0;
    }

    private void LeaveAltScreen()
    {
        if (!IsAltScreen) return;
        _active = _main;
        _placements.Clear(); // drop the alt screen's images (e.g. when docxy exits)
    }

    private void SaveCursor() { _savedRow = CursorRow; _savedCol = CursorCol; }

    private void RestoreCursor()
    {
        CursorRow = Math.Clamp(_savedRow, 0, Screen.Rows - 1);
        CursorCol = Math.Clamp(_savedCol, 0, Screen.Cols - 1);
    }

    private readonly Dictionary<int, KittyImage> _images = new();
    private readonly List<ImagePlacement> _placements = new();
    private readonly System.Text.StringBuilder _kittyChunks = new();
    private Dictionary<string, string>? _kittyKeys;

    /// <summary>Transmitted Kitty images, keyed by image id.</summary>
    public IReadOnlyDictionary<int, KittyImage> Images => _images;

    /// <summary>Image placements on the grid, in z-order of arrival.</summary>
    public IReadOnlyList<ImagePlacement> Placements => _placements;

    // ---- Direct image API (bypasses the APC/base64 text path) ----
    // Lets a host deliver already-decoded image bytes without a base64 round-trip through
    // the parser, so heavy work stays off the render lock. Semantics match a=T/a=p/a=d.

    /// <summary>Remove all image placements (equivalent of Kitty a=d with no id).</summary>
    public void ClearPlacements() => _placements.Clear();

    /// <summary>True if an image with this id has been transmitted (so it can be re-placed).</summary>
    public bool HasImage(int id) => _images.ContainsKey(id);

    /// <summary>Register/replace an image's pixel payload by id (equivalent of a=T's transmit).</summary>
    public void SetImageData(int id, KittyFormat format, int width, int height, byte[] data)
        => _images[id] = new KittyImage(id, format, width, height, data);

    /// <summary>
    /// Place (or re-place) an image at an explicit cell, replacing any placement of the same id.
    /// The optional pixel source rectangle (srcW/srcH &gt; 0) crops the image — used to scroll a
    /// cached texture by moving the visible window rather than re-transmitting cropped pixels.
    /// </summary>
    public void PlaceImage(int id, int row, int col, int cols, int rows,
        int srcX = 0, int srcY = 0, int srcW = 0, int srcH = 0)
    {
        _placements.RemoveAll(p => p.ImageId == id);
        _placements.Add(new ImagePlacement(id, row, col, cols, rows, srcX, srcY, srcW, srcH));
    }

    public void ApcDispatch(string data)
    {
        if (data.Length == 0 || data[0] != 'G') return; // only Kitty graphics (_G...)
        string body = data[1..];
        int semi = body.IndexOf(';');
        string control = semi >= 0 ? body[..semi] : body;
        string payload = semi >= 0 ? body[(semi + 1)..] : string.Empty;

        var keys = ParseKittyKeys(control);
        if (_kittyChunks.Length == 0) _kittyKeys = keys; // first chunk carries the metadata
        _kittyChunks.Append(payload);

        bool more = keys.TryGetValue("m", out var mv) && mv == "1";
        if (more) return; // accumulate until the final chunk (m=0 / absent)

        FinalizeKittyImage();
    }

    private void FinalizeKittyImage()
    {
        var keys = _kittyKeys ?? new Dictionary<string, string>();
        string b64 = _kittyChunks.ToString();
        _kittyChunks.Clear();
        _kittyKeys = null;

        int id = GetKittyInt(keys, "i", 0);
        var format = (KittyFormat)GetKittyInt(keys, "f", 32);
        int w = GetKittyInt(keys, "s", 0);
        int h = GetKittyInt(keys, "v", 0);
        string action = keys.TryGetValue("a", out var a) ? a : "t";

        if (action == "d") // delete: by id if i present, else all placements
        {
            if (id != 0) _placements.RemoveAll(p => p.ImageId == id);
            else _placements.Clear();
            return;
        }

        if (b64.Length > 0)
        {
            byte[] bytes;
            try { bytes = Convert.FromBase64String(b64); }
            catch (FormatException) { return; }
            _images[id] = new KittyImage(id, format, w, h, bytes);
        }

        // a=T (transmit+display) or a=p (put): place at the cursor, replacing any
        // existing placement of the same id so repeated redraws don't accumulate.
        if (action is "T" or "p")
        {
            int cols = GetKittyInt(keys, "c", 0);
            int rows = GetKittyInt(keys, "r", 0);
            _placements.RemoveAll(p => p.ImageId == id);
            _placements.Add(new ImagePlacement(id, CursorRow, CursorCol, cols, rows));
        }
    }

    private static Dictionary<string, string> ParseKittyKeys(string control)
    {
        var d = new Dictionary<string, string>();
        foreach (var pair in control.Split(','))
        {
            int eq = pair.IndexOf('=');
            if (eq > 0) d[pair[..eq]] = pair[(eq + 1)..];
        }
        return d;
    }

    private static int GetKittyInt(Dictionary<string, string> d, string key, int def)
        => d.TryGetValue(key, out var v) && int.TryParse(v, out var n) ? n : def;

    public void OscDispatch(int command, string text)
    {
        switch (command)
        {
            case 0:
            case 2:
                Title = text;
                break;
            case 7:
                Cwd = text;
                break;
        }
    }

    private void ApplySgr(IReadOnlyList<int> p)
    {
        if (p.Count == 0) { ResetPen(); return; }
        for (int i = 0; i < p.Count; i++)
        {
            int code = p[i];
            switch (code)
            {
                case 0: ResetPen(); break;
                case 1: _attrs |= CellAttributes.Bold; break;
                case 2: _attrs |= CellAttributes.Dim; break;
                case 3: _attrs |= CellAttributes.Italic; break;
                case 4: _attrs |= CellAttributes.Underline; break;
                case 7: _attrs |= CellAttributes.Inverse; break;
                case 22: _attrs &= ~(CellAttributes.Bold | CellAttributes.Dim); break;
                case 23: _attrs &= ~CellAttributes.Italic; break;
                case 24: _attrs &= ~CellAttributes.Underline; break;
                case 27: _attrs &= ~CellAttributes.Inverse; break;
                case >= 30 and <= 37: _fg = Color.FromIndex(code - 30); break;
                case 39: _fg = Color.DefaultForeground; break;
                case >= 40 and <= 47: _bg = Color.FromIndex(code - 40); break;
                case 49: _bg = Color.DefaultBackground; break;
                case >= 90 and <= 97: _fg = Color.FromIndex(code - 90 + 8); break;
                case >= 100 and <= 107: _bg = Color.FromIndex(code - 100 + 8); break;
                case 38: i = ExtendedColor(p, i, ref _fg); break;
                case 48: i = ExtendedColor(p, i, ref _bg); break;
            }
        }
    }

    private static int ExtendedColor(IReadOnlyList<int> p, int i, ref Color target)
    {
        if (i + 1 >= p.Count) return i;
        int mode = p[i + 1];
        if (mode == 5 && i + 2 < p.Count) { target = Color.FromIndex(p[i + 2]); return i + 2; }
        if (mode == 2 && i + 4 < p.Count) { target = new Color((byte)p[i + 2], (byte)p[i + 3], (byte)p[i + 4]); return i + 4; }
        return i + 1;
    }

    private void ResetPen()
    {
        _fg = Color.DefaultForeground;
        _bg = Color.DefaultBackground;
        _attrs = CellAttributes.None;
    }

    /// <summary>A blank cell carrying the current background (background-color-erase).</summary>
    private Cell Blank() => new(' ', Color.DefaultForeground, _bg, CellAttributes.None);

    private void EraseChars(int count)
    {
        int to = Math.Min(CursorCol + count, Screen.Cols);
        for (int c = CursorCol; c < to; c++)
            Screen[CursorRow, c] = Blank();
    }

    private void EraseLine(int mode)
    {
        int from = mode == 0 ? CursorCol : 0;
        int to = mode == 1 ? CursorCol : Screen.Cols - 1;
        for (int c = from; c <= to; c++) Screen[CursorRow, c] = Blank();
    }

    private void EraseDisplay(int mode)
    {
        if (mode == 2) // whole display, filled with the current background
        {
            for (int r = 0; r < Screen.Rows; r++)
                for (int c = 0; c < Screen.Cols; c++) Screen[r, c] = Blank();
            return;
        }
        if (mode == 0)
        {
            EraseLine(0);
            for (int r = CursorRow + 1; r < Screen.Rows; r++)
                for (int c = 0; c < Screen.Cols; c++) Screen[r, c] = Blank();
        }
        else if (mode == 1)
        {
            EraseLine(1);
            for (int r = 0; r < CursorRow; r++)
                for (int c = 0; c < Screen.Cols; c++) Screen[r, c] = Blank();
        }
    }

    private void SetScrollRegion(int top, int bottom)
    {
        top = Math.Clamp(top, 0, Screen.Rows - 1);
        bottom = Math.Clamp(bottom, 0, Screen.Rows - 1);
        if (top >= bottom) { top = 0; bottom = Screen.Rows - 1; } // invalid -> reset to full
        _scrollTop = top;
        _scrollBottom = bottom;
        CursorRow = 0;
        CursorCol = 0;
    }

    private void Index() // move down one line, scrolling the region at the bottom margin
    {
        if (CursorRow == _scrollBottom)
            ScrollRegionUp();
        else if (CursorRow < Screen.Rows - 1)
            CursorRow++;
    }

    private void ReverseIndex() // move up one line, scrolling the region at the top margin
    {
        if (CursorRow == _scrollTop)
            ScrollRegionDown();
        else if (CursorRow > 0)
            CursorRow--;
    }

    private void ScrollRegionUp()
    {
        for (int r = _scrollTop + 1; r <= _scrollBottom; r++)
            for (int c = 0; c < Screen.Cols; c++)
                Screen[r - 1, c] = Screen[r, c];
        for (int c = 0; c < Screen.Cols; c++)
            Screen[_scrollBottom, c] = Blank();
    }

    private void ScrollRegionDown()
    {
        for (int r = _scrollBottom; r > _scrollTop; r--)
            for (int c = 0; c < Screen.Cols; c++)
                Screen[r, c] = Screen[r - 1, c];
        for (int c = 0; c < Screen.Cols; c++)
            Screen[_scrollTop, c] = Blank();
    }

    private void InsertLines(int n) // IL: insert n blank lines at cursor row, within scroll region
    {
        if (CursorRow < _scrollTop || CursorRow > _scrollBottom) return;
        for (int i = 0; i < n; i++)
        {
            for (int r = _scrollBottom; r > CursorRow; r--)
                for (int c = 0; c < Screen.Cols; c++)
                    Screen[r, c] = Screen[r - 1, c];
            for (int c = 0; c < Screen.Cols; c++)
                Screen[CursorRow, c] = Blank();
        }
    }

    private void DeleteLines(int n) // DL: delete n lines at cursor row, pulling region up
    {
        if (CursorRow < _scrollTop || CursorRow > _scrollBottom) return;
        for (int i = 0; i < n; i++)
        {
            for (int r = CursorRow; r < _scrollBottom; r++)
                for (int c = 0; c < Screen.Cols; c++)
                    Screen[r, c] = Screen[r + 1, c];
            for (int c = 0; c < Screen.Cols; c++)
                Screen[_scrollBottom, c] = Blank();
        }
    }

    private void InsertChars(int n) // ICH: shift cells right at cursor, blank the gap
    {
        for (int c = Screen.Cols - 1; c >= CursorCol + n; c--)
            Screen[CursorRow, c] = Screen[CursorRow, c - n];
        for (int c = CursorCol; c < Math.Min(CursorCol + n, Screen.Cols); c++)
            Screen[CursorRow, c] = Blank();
    }

    private void DeleteChars(int n) // DCH: shift cells left at cursor, blank the tail
    {
        for (int c = CursorCol; c < Screen.Cols; c++)
            Screen[CursorRow, c] = c + n < Screen.Cols ? Screen[CursorRow, c + n] : Blank();
    }

    public string DumpRow(int row)
    {
        var sb = new System.Text.StringBuilder();
        for (int c = 0; c < Screen.Cols; c++)
        {
            Cell cell = Screen[row, c];
            if (cell.Width == 0) continue; // trailing spacer of a wide glyph
            sb.Append(cell.Rune);
        }
        return sb.ToString().TrimEnd();
    }
}
