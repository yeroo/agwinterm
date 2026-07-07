namespace Agwinterm.Core;

public sealed class VtParser(IParserPerformer performer)
{
    private const char Replacement = '�';

    private ParserState _state = ParserState.Ground;
    private readonly List<int> _params = new();
    private int _current;
    private bool _hasCurrent;
    private char _csiPrefix; // private-mode marker: < = > ? or '\0'

    // UTF-8 accumulator (used only in Ground state).
    private int _utf8Remaining;
    private int _utf8Accum;

    // OSC / APC string accumulators.
    private readonly List<byte> _osc = new();   // raw payload bytes; UTF-8-decoded at dispatch
    private readonly System.Text.StringBuilder _apc = new();

    public void Feed(ReadOnlySpan<byte> bytes)
    {
        foreach (byte b in bytes)
            Step(b);
    }

    private void Step(byte b)
    {
        // ESC inside an OSC/APC string may be the start of a String Terminator (ESC \).
        if (b == 0x1b && _state == ParserState.OscString) { _state = ParserState.OscEsc; return; }
        if (b == 0x1b && _state == ParserState.ApcString) { _state = ParserState.ApcEsc; return; }

        // ESC otherwise restarts an escape sequence from any state.
        if (b == 0x1b) { FlushIncompleteUtf8(); EnterEscape(); return; }

        switch (_state)
        {
            case ParserState.Ground:
                GroundByte(b);
                break;

            case ParserState.Escape:
                if (b == (byte)'[') { _state = ParserState.CsiEntry; ResetParams(); }
                else if (b == (byte)']') { _state = ParserState.OscString; _osc.Clear(); }
                else if (b == (byte)'_') { _state = ParserState.ApcString; _apc.Clear(); }
                else if (b is >= 0x30 and <= 0x7e) { performer.EscDispatch((char)b); _state = ParserState.Ground; }
                else if (IsControl(b)) performer.Execute(b);
                else _state = ParserState.Ground;
                break;

            case ParserState.OscString:
                if (b == 0x07) { DispatchOsc(); _state = ParserState.Ground; } // BEL terminator
                else _osc.Add(b);
                break;

            case ParserState.OscEsc:
                DispatchOsc();
                _state = ParserState.Ground;
                if (b != (byte)'\\') Step(b); // not ST: reprocess this byte
                break;

            case ParserState.ApcString:
                if (b == 0x07) { DispatchApc(); _state = ParserState.Ground; } // BEL terminator
                else _apc.Append((char)b);
                break;

            case ParserState.ApcEsc:
                DispatchApc();
                _state = ParserState.Ground;
                if (b != (byte)'\\') Step(b);
                break;

            case ParserState.CsiEntry:
            case ParserState.CsiParam:
                if (b is >= (byte)'0' and <= (byte)'9') { _current = _current * 10 + (b - '0'); _hasCurrent = true; _state = ParserState.CsiParam; }
                else if (b == (byte)';') { PushParam(); _state = ParserState.CsiParam; }
                else if (b is >= 0x3c and <= 0x3f) { _csiPrefix = (char)b; } // private marker < = > ?
                else if (b is >= 0x40 and <= 0x7e) { PushParamIfAny(); performer.CsiDispatch((char)b, _params, _csiPrefix); _state = ParserState.Ground; }
                else if (b is >= 0x20 and <= 0x2f) { _state = ParserState.CsiIntermediate; }
                else if (IsControl(b)) performer.Execute(b);
                else _state = ParserState.CsiIgnore;
                break;

            case ParserState.CsiIntermediate:
                if (b is >= 0x40 and <= 0x7e) { PushParamIfAny(); performer.CsiDispatch((char)b, _params, _csiPrefix); _state = ParserState.Ground; }
                else if (b is >= 0x20 and <= 0x2f) { /* collect intermediates: ignored for now */ }
                else _state = ParserState.CsiIgnore;
                break;

            case ParserState.CsiIgnore:
                if (b is >= 0x40 and <= 0x7e) _state = ParserState.Ground;
                break;
        }
    }

    private void GroundByte(byte b)
    {
        if (_utf8Remaining > 0)
        {
            if ((b & 0xC0) == 0x80) // valid continuation
            {
                _utf8Accum = (_utf8Accum << 6) | (b & 0x3F);
                if (--_utf8Remaining == 0) EmitScalar(_utf8Accum);
                return;
            }
            // Invalid continuation: flush the incomplete sequence, then reprocess b fresh.
            _utf8Remaining = 0;
            performer.Print(Replacement);
        }

        if (b < 0x80)
        {
            if (IsControl(b)) performer.Execute(b);
            else performer.Print((char)b);
        }
        else if ((b & 0xE0) == 0xC0) { _utf8Remaining = 1; _utf8Accum = b & 0x1F; }
        else if ((b & 0xF0) == 0xE0) { _utf8Remaining = 2; _utf8Accum = b & 0x0F; }
        else if ((b & 0xF8) == 0xF0) { _utf8Remaining = 3; _utf8Accum = b & 0x07; }
        else performer.Print(Replacement); // stray continuation or invalid lead byte
    }

    private void DispatchOsc()
    {
        // Decode the payload as UTF-8 (titles/cwd/notifications may be non-ASCII); invalid
        // sequences become U+FFFD. Byte-as-char accumulation would mojibake multibyte text.
        string s = System.Text.Encoding.UTF8.GetString(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_osc));
        int sep = s.IndexOf(';');
        string head = sep >= 0 ? s[..sep] : s;
        string text = sep >= 0 ? s[(sep + 1)..] : string.Empty;
        if (int.TryParse(head, out int command))
            performer.OscDispatch(command, text);
    }

    private void DispatchApc()
    {
        if (_apc.Length > 0)
            performer.ApcDispatch(_apc.ToString());
    }

    private void EmitScalar(int scalar)
    {
        // v1 limitation: BMP only. Astral codepoints and surrogates -> replacement.
        if (scalar is >= 0xD800 and <= 0xDFFF or > 0xFFFF)
            performer.Print(Replacement);
        else
            performer.Print((char)scalar);
    }

    private void FlushIncompleteUtf8()
    {
        if (_utf8Remaining > 0)
        {
            _utf8Remaining = 0;
            performer.Print(Replacement);
        }
    }

    private void EnterEscape()
    {
        _state = ParserState.Escape;
        ResetParams();
    }

    private static bool IsControl(byte b) => b < 0x20 || b == 0x7f;

    private void ResetParams()
    {
        _params.Clear();
        _current = 0;
        _hasCurrent = false;
        _csiPrefix = '\0';
    }

    private void PushParam()
    {
        _params.Add(_hasCurrent ? _current : 0);
        _current = 0;
        _hasCurrent = false;
    }

    private void PushParamIfAny()
    {
        if (_hasCurrent || _params.Count > 0)
            _params.Add(_hasCurrent ? _current : 0);
    }
}
