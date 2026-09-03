namespace Agwinterm.Core;

public sealed class VtParser(IParserPerformer performer)
{
    internal const int MaxStringPayloadBytes = 8_000_000;
    internal const int MaxCsiParameters = 256;
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
    private readonly List<byte> _dcs = new();   // DCS payload bytes (sixel etc.)
    private readonly System.Text.StringBuilder _apc = new();
    private bool _oscDiscarding;
    private bool _apcDiscarding;

    public void Feed(ReadOnlySpan<byte> bytes)
    {
        foreach (byte b in bytes)
            Step(b);
    }

    private void Step(byte b)
    {
        // ESC inside an OSC/APC/DCS string may be the start of a String Terminator (ESC \).
        if (b == 0x1b && _state == ParserState.OscString) { _state = ParserState.OscEsc; return; }
        if (b == 0x1b && _state == ParserState.ApcString) { _state = ParserState.ApcEsc; return; }
        if (b == 0x1b && _state == ParserState.DcsString) { _state = ParserState.DcsEsc; return; }

        // ESC otherwise restarts an escape sequence from any state.
        if (b == 0x1b) { FlushIncompleteUtf8(); EnterEscape(); return; }

        switch (_state)
        {
            case ParserState.Ground:
                GroundByte(b);
                break;

            case ParserState.Escape:
                if (b == (byte)'[') { _state = ParserState.CsiEntry; ResetParams(); }
                else if (b == (byte)']') { _state = ParserState.OscString; _osc.Clear(); _oscDiscarding = false; }
                else if (b == (byte)'_') { _state = ParserState.ApcString; _apc.Clear(); _apcDiscarding = false; }
                else if (b == (byte)'P') { _state = ParserState.DcsString; _dcs.Clear(); }   // DCS (sixel etc.)
                else if (b is >= 0x30 and <= 0x7e) { performer.EscDispatch((char)b); _state = ParserState.Ground; }
                else if (IsControl(b)) performer.Execute(b);
                else _state = ParserState.Ground;
                break;

            case ParserState.OscString:
                if (b == 0x07) { FinishOsc(); _state = ParserState.Ground; } // BEL terminator
                else if (!_oscDiscarding)
                {
                    if (_osc.Count < MaxStringPayloadBytes) _osc.Add(b);
                    else { _osc.Clear(); _oscDiscarding = true; }
                }
                break;

            case ParserState.OscEsc:
                FinishOsc();
                _state = ParserState.Ground;
                if (b != (byte)'\\') Step(b); // not ST: reprocess this byte
                break;

            case ParserState.ApcString:
                if (b == 0x07) { FinishApc(); _state = ParserState.Ground; } // BEL terminator
                else if (!_apcDiscarding)
                {
                    if (_apc.Length < MaxStringPayloadBytes) _apc.Append((char)b);
                    else { _apc.Clear(); _apcDiscarding = true; }
                }
                break;

            case ParserState.ApcEsc:
                FinishApc();
                _state = ParserState.Ground;
                if (b != (byte)'\\') Step(b);
                break;

            case ParserState.DcsString:
                if (b == 0x07) { DispatchDcs(); _state = ParserState.Ground; } // BEL terminator
                else if (_dcs.Count < 8_000_000) _dcs.Add(b);                  // cap runaway payloads
                break;

            case ParserState.DcsEsc:
                DispatchDcs();
                _state = ParserState.Ground;
                if (b != (byte)'\\') Step(b);
                break;

            case ParserState.CsiEntry:
            case ParserState.CsiParam:
                if (b is >= (byte)'0' and <= (byte)'9') { _current = _current * 10 + (b - '0'); _hasCurrent = true; _state = ParserState.CsiParam; }
                else if (b == (byte)';')
                {
                    if (TryPushParam()) _state = ParserState.CsiParam;
                    else DiscardCsi();
                }
                else if (b is >= 0x3c and <= 0x3f) { _csiPrefix = (char)b; } // private marker < = > ?
                else if (b is >= 0x40 and <= 0x7e) FinishCsi(b);
                else if (b is >= 0x20 and <= 0x2f) { _state = ParserState.CsiIntermediate; }
                else if (IsControl(b)) performer.Execute(b);
                else _state = ParserState.CsiIgnore;
                break;

            case ParserState.CsiIntermediate:
                if (b is >= 0x40 and <= 0x7e) FinishCsi(b);
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

    private void FinishOsc()
    {
        if (!_oscDiscarding) DispatchOsc();
        _osc.Clear();
        _oscDiscarding = false;
    }

    private void DispatchApc()
    {
        if (_apc.Length > 0)
            performer.ApcDispatch(_apc.ToString());
    }

    private void FinishApc()
    {
        if (!_apcDiscarding) DispatchApc();
        _apc.Clear();
        _apcDiscarding = false;
    }

    private void DispatchDcs()
    {
        if (_dcs.Count > 0)
            performer.DcsDispatch(_dcs.ToArray());
    }

    private void EmitScalar(int scalar)
    {
        // Raw surrogates / out-of-range -> replacement.
        if (scalar is >= 0xD800 and <= 0xDFFF or > 0x10FFFF or < 0)
        {
            performer.Print(Replacement);
            return;
        }
        if (scalar > 0xFFFF)
        {
            // Astral (emoji, nerd-font plane-15/16 icons): hand the performer the surrogate pair;
            // the emulator re-pairs it into a single cell.
            performer.Print((char)(0xD800 + ((scalar - 0x10000) >> 10)));
            performer.Print((char)(0xDC00 + ((scalar - 0x10000) & 0x3FF)));
            return;
        }
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

    private bool TryPushParam()
    {
        if (_params.Count >= MaxCsiParameters) return false;
        _params.Add(_hasCurrent ? _current : 0);
        _current = 0;
        _hasCurrent = false;
        return true;
    }

    private bool TryPushParamIfAny()
    {
        if (_hasCurrent || _params.Count > 0)
            return TryPushParam();
        return true;
    }

    private void FinishCsi(byte final)
    {
        if (TryPushParamIfAny())
            performer.CsiDispatch((char)final, _params, _csiPrefix);
        else
            ResetParams();
        _state = ParserState.Ground;
    }

    private void DiscardCsi()
    {
        // A child can leave a CSI split across arbitrarily many reads. Drop the retained list as
        // soon as it exceeds the bound, then ignore through the final byte (or a restarting ESC).
        ResetParams();
        _state = ParserState.CsiIgnore;
    }
}
