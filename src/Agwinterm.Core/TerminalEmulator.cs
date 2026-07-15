using System.Text;

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

    /// <summary>Bracketed paste mode (DECSET 2004): wrap pasted text in ESC[200~ … ESC[201~.</summary>
    public bool BracketedPaste { get; private set; }

    private int _scrollTop;
    private int _scrollBottom;

    // Scrollback: rows scrolled off the TOP of the MAIN screen (oldest first). Trimmed in batches
    // to amortise the cost, so the count can float up to ScrollbackMax + a small slack.
    private readonly List<Cell[]> _history = new();
    private const int TrimSlack = 512;

    /// <summary>Max scrollback rows to keep (0 disables history; excess is dropped oldest-first).</summary>
    public int ScrollbackMax { get; set; } = 5000;

    /// <summary>Rows of scrollback available above the live screen (main screen only).</summary>
    public int HistoryCount => _history.Count;

    /// <summary>Monotonic count of lines ever scrolled into history. Unlike <see cref="HistoryCount"/> this
    /// keeps climbing after scrollback fills (eviction), so it's a reliable "the viewport content shifted"
    /// signal. Stays flat during in-place repaints (TUIs like Claude Code redrawing without scrolling),
    /// which lets a mouse selection survive those repaints.</summary>
    public long ScrollGeneration { get; private set; }

    /// <summary>Read a scrolled-off cell; history row 0 is the oldest. Pads to the current width.</summary>
    public Cell GetHistoryCell(int historyRow, int col)
    {
        if ((uint)historyRow >= (uint)_history.Count) return Cell.Empty;
        var row = _history[historyRow];
        return (uint)col < (uint)row.Length ? row[col] : Cell.Empty;
    }

    private void PushHistory()
    {
        int cols = Screen.Cols;
        var row = new Cell[cols];
        Screen.CopyRowTo(0, row);
        _history.Add(row);
        ScrollGeneration++;
        if (_history.Count > ScrollbackMax + TrimSlack)
        {
            int trim = _history.Count - ScrollbackMax;
            _history.RemoveRange(0, trim);
            // Marks address buffer-absolute lines (history + live row): shift with the trim.
            for (int i = _marks.Count - 1; i >= 0; i--)
            {
                var m = _marks[i];
                m.PromptLine -= trim;
                if (m.CommandLine >= 0) m.CommandLine -= trim;
                if (m.OutputLine >= 0) m.OutputLine -= trim;
                if (m.EndLine >= 0) m.EndLine -= trim;
                if (m.PromptLine < 0) _marks.RemoveAt(i);
            }
        }
    }

    // ---- Shell-integration marks (FTCS / OSC 133): prompt + command-output boundaries ----

    /// <summary>One prompt→command→output record from FTCS (OSC 133). Lines are buffer-absolute
    /// (history + live row); they shift as history trims and drop off when trimmed away.</summary>
    public sealed class ShellMark
    {
        public int PromptLine;        // 133;A — the prompt started on this line
        public int CommandLine = -1;  // 133;B — command input line (where the user types)
        public int OutputLine = -1;   // 133;C — command output starts here (optional)
        public int EndLine = -1;      // 133;D — command finished (the next prompt's line)
        public int? ExitCode;         // from 133;D;<code>
    }

    private readonly List<ShellMark> _marks = new();

    /// <summary>FTCS marks, oldest first (main screen only; capped at 512).</summary>
    public IReadOnlyList<ShellMark> Marks => _marks;

    private void FtcsDispatch(string text)
    {
        if (IsAltScreen || text.Length == 0) return;
        int abs = _history.Count + CursorRow;
        switch (char.ToUpperInvariant(text[0]))
        {
            case 'A':   // prompt start (dedupe re-renders on the same line)
                if (_marks.Count == 0 || _marks[^1].PromptLine != abs)
                    _marks.Add(new ShellMark { PromptLine = abs });
                break;
            case 'B':   // command-line start (user input row)
                if (_marks.Count > 0 && _marks[^1].CommandLine < 0) _marks[^1].CommandLine = abs;
                break;
            case 'C':   // output start
                if (_marks.Count > 0 && _marks[^1].OutputLine < 0) _marks[^1].OutputLine = abs;
                break;
            case 'D':   // command finished (+ optional exit code)
                if (_marks.Count > 0 && _marks[^1].EndLine < 0)
                {
                    var m = _marks[^1];
                    m.EndLine = abs;
                    int semi = text.IndexOf(';');
                    if (semi >= 0 && int.TryParse(text[(semi + 1)..], out int ec)) m.ExitCode = ec;
                }
                break;
            // 'B' (command-line start) isn't tracked separately in v1.
        }
        if (_marks.Count > 512) _marks.RemoveRange(0, _marks.Count - 512);
    }

    private Color _fg = Color.DefaultForeground;
    private Color _bg = Color.DefaultBackground;
    private ColorSpec _fgSpec = ColorSpec.Default;   // semantic colour (default/indexed/rgb) for theming
    private ColorSpec _bgSpec = ColorSpec.Default;
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

    private char _pendingHighSurrogate;

    public void Print(char ch)
    {
        // Astral codepoints arrive as a surrogate pair (two Print calls); re-pair into one cell.
        if (char.IsHighSurrogate(ch)) { _pendingHighSurrogate = ch; return; }
        if (char.IsLowSurrogate(ch))
        {
            if (_pendingHighSurrogate != '\0') PrintScalar(char.ConvertToUtf32(_pendingHighSurrogate, ch));
            _pendingHighSurrogate = '\0';
            return;
        }
        _pendingHighSurrogate = '\0';
        PrintScalar(ch);
    }

    private void PrintScalar(int cp)
    {
        int w = Wcwidth.Of(cp);
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

        Screen[CursorRow, CursorCol] = new Cell(cp, _fg, _bg, _attrs, (byte)w, _fgSpec, _bgSpec);
        if (w == 2)
            Screen[CursorRow, CursorCol + 1] = new Cell('\0', _fg, _bg, _attrs, 0, _fgSpec, _bgSpec); // trailing spacer
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

    // ---- Kitty keyboard protocol (progressive enhancement) ----
    // Flags: 1 disambiguate escape codes, 2 report event types, 4 report alternate keys,
    // 8 report all keys as escape codes, 16 report associated text. Apps push/pop a stack.
    private readonly Stack<int> _kbdStack = new();

    /// <summary>Active Kitty keyboard flags (0 = legacy encoding).</summary>
    public int KeyboardFlags => _kbdStack.Count > 0 ? _kbdStack.Peek() : 0;

    /// <summary>Raised when the terminal must write a reply to the shell (Kitty query response, etc.).
    /// The host wires this to the PTY's input.</summary>
    public event Action<string>? Respond;

    public void CsiDispatch(char final, IReadOnlyList<int> parameters, char prefix)
    {
        int P(int index, int def) =>
            index < parameters.Count && parameters[index] != 0 ? parameters[index] : def;

        // Kitty keyboard protocol: CSI ? u (query), CSI > flags u (push), CSI = flags;mode u (set),
        // CSI < n u (pop n).
        if (final == 'u' && prefix is '?' or '>' or '=' or '<')
        {
            switch (prefix)
            {
                case '?': Respond?.Invoke($"\x1b[?{KeyboardFlags}u"); break;   // report current flags
                case '>': _kbdStack.Push(parameters.Count > 0 ? parameters[0] : 0); break;
                case '=':
                {
                    int flags = parameters.Count > 0 ? parameters[0] : 0;
                    int mode = parameters.Count > 1 ? parameters[1] : 1;   // 1 set, 2 or-in, 3 and-not
                    int cur = KeyboardFlags, next = mode switch { 2 => cur | flags, 3 => cur & ~flags, _ => flags };
                    if (_kbdStack.Count > 0) _kbdStack.Pop();
                    _kbdStack.Push(next);
                    break;
                }
                case '<': for (int n = Math.Max(1, parameters.Count > 0 ? parameters[0] : 1); n > 0 && _kbdStack.Count > 0; n--) _kbdStack.Pop(); break;
            }
            return;
        }

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
                case 2004: BracketedPaste = set; break; // bracketed paste
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

    /// <summary>Cell size in pixels (set by the host from its font metrics) — used to lay a sixel
    /// image onto the grid and advance the cursor below it. Sane defaults for headless use.</summary>
    public int CellPixelWidth { get; set; } = 8;
    public int CellPixelHeight { get; set; } = 18;

    /// <summary>Next free image id for host-generated (sixel) images; negative to avoid clashing with
    /// Kitty ids (which are app-chosen positive numbers).</summary>
    private int _sixelSeq = -1;

    public void DcsDispatch(byte[] data) => PlaceSixel(data);

    /// <summary>Decode a sixel DCS payload and place it at the cursor (advancing below it). Returns
    /// false if it isn't a valid sixel. Reused by the out-of-band control path (ConPTY strips DCS
    /// through the shell, so sixel is also deliverable via the control pipe, like Kitty images).</summary>
    public bool PlaceSixel(byte[] data)
    {
        if (Sixel.Decode(data) is not { } s || s.Width <= 0 || s.Height <= 0) return false;
        int id = _sixelSeq--;
        _images[id] = new KittyImage(id, KittyFormat.Rgba, s.Width, s.Height, s.Rgba);
        int cols = Math.Max(1, (s.Width + CellPixelWidth - 1) / CellPixelWidth);
        int rows = Math.Max(1, (s.Height + CellPixelHeight - 1) / CellPixelHeight);
        _placements.Add(new ImagePlacement(id, CursorRow, CursorCol, cols, rows));   // visible grid row
        for (int k = 0; k < rows; k++) Index();   // advance the cursor below the image (sixel scrolling)
        CursorCol = 0;
        return true;
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

    /// <summary>Raised when the program requests a desktop notification via OSC 9 or OSC 777
    /// (title, body). Fires on the feed/pump thread — consumers must marshal to their UI thread.</summary>
    public event Action<string, string>? Notified;

    /// <summary>Raised for OSC 9;4 progress reports (ConEmu/Windows Terminal convention):
    /// (state, value) where state = 0 clear | 1 normal | 2 error | 3 indeterminate | 4 paused,
    /// value = 0–100 for state 1/2/4. Fires on the feed/pump thread.</summary>
    public event Action<int, int>? Progress;

    /// <summary>Strip C0/C1 control characters (and DEL) from an OSC payload. OSC strings feed the
    /// window title, the tracked cwd (later expanded into custom-command text), and notification
    /// toasts — embedded control bytes there are a terminal/shell-injection vector, so they are
    /// dropped at the dispatch boundary (the parser only terminates on BEL/ST; other C0s pass).</summary>
    private static string StripControls(string s)
    {
        bool clean = true;
        foreach (char c in s)
            if (c < '\x20' || c == '\x7f' || (c >= '\x80' && c <= '\x9f')) { clean = false; break; }
        if (clean) return s;
        var sb = new StringBuilder(s.Length);
        foreach (char c in s)
            if (c >= '\x20' && c != '\x7f' && (c < '\x80' || c > '\x9f')) sb.Append(c);
        return sb.ToString();
    }

    public void OscDispatch(int command, string text)
    {
        text = StripControls(text);
        switch (command)
        {
            case 0:
            case 2:
                Title = text;
                break;
            case 7:
                Cwd = text;
                break;
            case 133: // FTCS shell-integration marks: A prompt, B command, C output, D;exit done
                FtcsDispatch(text);
                break;
            case 9: // OSC 9 ; <message> — body-only desktop notification; OSC 9;4 = ConEmu/WT progress
                if (text.StartsWith("4;", StringComparison.Ordinal))
                {
                    // 9;4;<state>;<value> — state: 0 clear, 1 normal (value 0-100), 2 error,
                    // 3 indeterminate, 4 paused. Progress, not a notification.
                    var pp = text.Split(';');
                    int pState = pp.Length > 1 && int.TryParse(pp[1], out int s9) ? s9 : 0;
                    int pValue = pp.Length > 2 && int.TryParse(pp[2], out int v9) ? Math.Clamp(v9, 0, 100) : 0;
                    Progress?.Invoke(pState, pValue);
                    break;
                }
                Notified?.Invoke("", text);
                break;
            case 777: // OSC 777 ; notify ; <title> ; <body>
                var parts = text.Split(';');
                if (parts.Length >= 2 && parts[0] == "notify")
                    Notified?.Invoke(parts[1], parts.Length >= 3 ? string.Join(';', parts[2..]) : "");
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
                case >= 30 and <= 37: _fg = Color.FromIndex(code - 30); _fgSpec = ColorSpec.Indexed(code - 30); break;
                case 39: _fg = Color.DefaultForeground; _fgSpec = ColorSpec.Default; break;
                case >= 40 and <= 47: _bg = Color.FromIndex(code - 40); _bgSpec = ColorSpec.Indexed(code - 40); break;
                case 49: _bg = Color.DefaultBackground; _bgSpec = ColorSpec.Default; break;
                case >= 90 and <= 97: _fg = Color.FromIndex(code - 90 + 8); _fgSpec = ColorSpec.Indexed(code - 90 + 8); break;
                case >= 100 and <= 107: _bg = Color.FromIndex(code - 100 + 8); _bgSpec = ColorSpec.Indexed(code - 100 + 8); break;
                case 38: i = ExtendedColor(p, i, ref _fg, ref _fgSpec); break;
                case 48: i = ExtendedColor(p, i, ref _bg, ref _bgSpec); break;
            }
        }
    }

    private static int ExtendedColor(IReadOnlyList<int> p, int i, ref Color target, ref ColorSpec spec)
    {
        if (i + 1 >= p.Count) return i;
        int mode = p[i + 1];
        if (mode == 5 && i + 2 < p.Count) { target = Color.FromIndex(p[i + 2]); spec = ColorSpec.Indexed(p[i + 2]); return i + 2; }
        if (mode == 2 && i + 4 < p.Count) { var c = new Color((byte)p[i + 2], (byte)p[i + 3], (byte)p[i + 4]); target = c; spec = ColorSpec.FromRgb(c); return i + 4; }
        return i + 1;
    }

    private void ResetPen()
    {
        _fg = Color.DefaultForeground;
        _bg = Color.DefaultBackground;
        _fgSpec = ColorSpec.Default;
        _bgSpec = ColorSpec.Default;
        _attrs = CellAttributes.None;
    }

    /// <summary>A blank cell carrying the current background (background-color-erase).</summary>
    private Cell Blank() => new(' ', Color.DefaultForeground, _bg, CellAttributes.None, 1, ColorSpec.Default, _bgSpec);

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
        // A full-height scroll on the main screen pushes the top row into scrollback.
        if (!IsAltScreen && ScrollbackMax > 0 && _scrollTop == 0 && _scrollBottom == Screen.Rows - 1)
            PushHistory();
        // Sixel images (negative ids; not host-managed like Kitty) scroll up with the text.
        if (_placements.Count > 0)
            for (int i = _placements.Count - 1; i >= 0; i--)
                if (_placements[i].ImageId < 0)
                {
                    var np = _placements[i] with { Row = _placements[i].Row - 1 };
                    if (np.Row + Math.Max(1, np.Rows) <= 0) { _placements.RemoveAt(i); _images.Remove(np.ImageId); }
                    else _placements[i] = np;
                }
        // One memmove for the region, not rows×cols indexer calls (this is the hottest path
        // under sustained output — every LF at the bottom row lands here).
        Screen.MoveRows(_scrollTop + 1, _scrollTop, _scrollBottom - _scrollTop);
        Screen.FillRow(_scrollBottom, Blank());
    }

    private void ScrollRegionDown()
    {
        Screen.MoveRows(_scrollTop, _scrollTop + 1, _scrollBottom - _scrollTop);
        Screen.FillRow(_scrollTop, Blank());
    }

    private void InsertLines(int n) // IL: insert n blank lines at cursor row, within scroll region
    {
        if (CursorRow < _scrollTop || CursorRow > _scrollBottom) return;
        n = Math.Min(n, _scrollBottom - CursorRow + 1);
        int shift = _scrollBottom - CursorRow + 1 - n;
        if (shift > 0) Screen.MoveRows(CursorRow, CursorRow + n, shift);
        for (int r = CursorRow; r < CursorRow + n; r++) Screen.FillRow(r, Blank());
    }

    private void DeleteLines(int n) // DL: delete n lines at cursor row, pulling region up
    {
        if (CursorRow < _scrollTop || CursorRow > _scrollBottom) return;
        n = Math.Min(n, _scrollBottom - CursorRow + 1);
        int shift = _scrollBottom - CursorRow + 1 - n;
        if (shift > 0) Screen.MoveRows(CursorRow + n, CursorRow, shift);
        for (int r = _scrollBottom - n + 1; r <= _scrollBottom; r++) Screen.FillRow(r, Blank());
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
            if (cell.Rune > 0xFFFF) sb.Append(char.ConvertFromUtf32(cell.Rune));
            else sb.Append((char)cell.Rune);
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>Plain text of one scrollback row (same conventions as <see cref="DumpRow"/>).</summary>
    public string DumpHistoryRow(int index)
    {
        var sb = new System.Text.StringBuilder();
        for (int c = 0; c < Screen.Cols; c++)
        {
            Cell cell = GetHistoryCell(index, c);
            if (cell.Width == 0) continue;
            if (cell.Rune > 0xFFFF) sb.Append(char.ConvertFromUtf32(cell.Rune));
            else sb.Append((char)cell.Rune);
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>The whole buffer as plain-text lines (history + visible), trailing blank rows dropped —
    /// for buffer-content persistence.</summary>
    public IReadOnlyList<string> DumpBuffer()
    {
        var lines = new List<string>();
        for (int h = 0; h < _history.Count; h++)
        {
            var row = _history[h];
            var sb = new System.Text.StringBuilder();
            foreach (var cell in row)
            {
                if (cell.Width == 0) continue;
                if (cell.Rune > 0xFFFF) sb.Append(char.ConvertFromUtf32(cell.Rune));
                else sb.Append(cell.Rune == 0 ? ' ' : (char)cell.Rune);
            }
            lines.Add(sb.ToString().TrimEnd());
        }
        for (int r = 0; r < Screen.Rows; r++) lines.Add(DumpRow(r));
        while (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
        return lines;
    }

    /// <summary>Seed the scrollback with plain-text lines (dimmed) — restores a prior session's buffer
    /// above the fresh shell without touching the live screen.</summary>
    public void SeedScrollback(IReadOnlyList<string> lines)
    {
        int cols = Screen.Cols;
        foreach (var line in lines)
        {
            var row = new Cell[cols];
            for (int c = 0; c < cols; c++)
            {
                char ch = c < line.Length ? line[c] : ' ';
                row[c] = new Cell(ch, Color.DefaultForeground, Color.DefaultBackground, CellAttributes.Dim);
            }
            _history.Add(row);
        }
        if (_history.Count > ScrollbackMax + TrimSlack)
            _history.RemoveRange(0, _history.Count - ScrollbackMax);
    }
}
