//! Faithful port of Agwinterm.Core/VtParser.cs — the VT escape-sequence state
//! machine. Event-for-event identical to the C# parser, including its quirks:
//!  - Print emits UTF-16 CODE UNITS (astral scalars arrive as a surrogate pair),
//!    because the C# performer contract is char-based.
//!  - CSI numeric values use UNCHECKED i32 overflow (C# unchecked wrap), while parameter count is
//!    capped so a child cannot retain an unbounded list across feeds.
//!  - APC payload accumulates bytes-as-chars (latin1), OSC decodes UTF-8 with
//!    U+FFFD replacement, and OSC/APC/DCS payload storage is capped at 8 MB.
//!  - Controls inside CsiEntry/CsiParam Execute mid-sequence; CsiIgnore
//!    swallows them; Escape state Executes controls WITHOUT leaving Escape.
//!  - Overlong UTF-8 encodings are NOT rejected (they decode and print).
//!
//! The differential oracle feeds identical byte streams to both parsers and
//! compares the full recorded event streams.

const REPLACEMENT: u16 = 0xFFFD;
const MAX_STRING_PAYLOAD_BYTES: usize = 8_000_000;
const MAX_CSI_PARAMETERS: usize = 256;

pub trait Performer {
    fn print(&mut self, ch: u16);
    fn execute(&mut self, byte: u8);
    fn esc_dispatch(&mut self, ch: u8);
    fn csi_dispatch(&mut self, ch: u8, params: &[i32], prefix: u8);
    fn osc_dispatch(&mut self, command: i32, text: &str);
    fn apc_dispatch(&mut self, text: &str);
    fn dcs_dispatch(&mut self, payload: &[u8]);
}

#[derive(Clone, Copy, PartialEq, Eq)]
enum State {
    Ground,
    Escape,
    CsiEntry,
    CsiParam,
    CsiIntermediate,
    CsiIgnore,
    OscString,
    OscEsc,
    ApcString,
    ApcEsc,
    DcsString,
    DcsEsc,
}

pub struct VtParser {
    state: State,
    params: Vec<i32>,
    current: i32,
    has_current: bool,
    csi_prefix: u8, // private-mode marker: < = > ? or 0

    utf8_remaining: u32,
    utf8_accum: i32,

    osc: Vec<u8>,
    dcs: Vec<u8>,
    apc: String, // C# accumulates (char)b — latin1 byte-as-char
    osc_discarding: bool,
    apc_discarding: bool,
    apc_payload_bytes: usize,
}

impl Default for VtParser {
    fn default() -> Self {
        Self::new()
    }
}

impl VtParser {
    pub fn new() -> VtParser {
        VtParser {
            state: State::Ground,
            params: Vec::new(),
            current: 0,
            has_current: false,
            csi_prefix: 0,
            utf8_remaining: 0,
            utf8_accum: 0,
            osc: Vec::new(),
            dcs: Vec::new(),
            apc: String::new(),
            osc_discarding: false,
            apc_discarding: false,
            apc_payload_bytes: 0,
        }
    }

    pub fn feed(&mut self, bytes: &[u8], p: &mut impl Performer) {
        for &b in bytes {
            self.step(b, p);
        }
    }

    fn step(&mut self, b: u8, p: &mut impl Performer) {
        // ESC inside an OSC/APC/DCS string may be the start of a String Terminator (ESC \).
        if b == 0x1b && self.state == State::OscString {
            self.state = State::OscEsc;
            return;
        }
        if b == 0x1b && self.state == State::ApcString {
            self.state = State::ApcEsc;
            return;
        }
        if b == 0x1b && self.state == State::DcsString {
            self.state = State::DcsEsc;
            return;
        }

        // ESC otherwise restarts an escape sequence from any state.
        if b == 0x1b {
            self.flush_incomplete_utf8(p);
            self.state = State::Escape;
            self.reset_params();
            return;
        }

        match self.state {
            State::Ground => self.ground_byte(b, p),

            State::Escape => {
                if b == b'[' {
                    self.state = State::CsiEntry;
                    self.reset_params();
                } else if b == b']' {
                    self.state = State::OscString;
                    self.osc.clear();
                    self.osc_discarding = false;
                } else if b == b'_' {
                    self.state = State::ApcString;
                    self.apc.clear();
                    self.apc_discarding = false;
                    self.apc_payload_bytes = 0;
                } else if b == b'P' {
                    self.state = State::DcsString;
                    self.dcs.clear();
                } else if (0x30..=0x7e).contains(&b) {
                    p.esc_dispatch(b);
                    self.state = State::Ground;
                } else if is_control(b) {
                    p.execute(b);
                }
                // stays in Escape, like the C# original
                else {
                    self.state = State::Ground;
                }
            }

            State::OscString => {
                if b == 0x07 {
                    self.finish_osc(p);
                    self.state = State::Ground;
                } else if !self.osc_discarding && self.osc.len() < MAX_STRING_PAYLOAD_BYTES {
                    self.osc.push(b);
                } else if !self.osc_discarding {
                    self.osc.clear();
                    self.osc_discarding = true;
                }
            }

            State::OscEsc => {
                self.finish_osc(p);
                self.state = State::Ground;
                if b != b'\\' {
                    self.step(b, p);
                } // not ST: reprocess this byte
            }

            State::ApcString => {
                if b == 0x07 {
                    self.finish_apc(p);
                    self.state = State::Ground;
                } else if !self.apc_discarding && self.apc_payload_bytes < MAX_STRING_PAYLOAD_BYTES
                {
                    self.apc.push(b as char);
                    self.apc_payload_bytes += 1;
                } else if !self.apc_discarding {
                    self.apc.clear();
                    self.apc_payload_bytes = 0;
                    self.apc_discarding = true;
                }
            }

            State::ApcEsc => {
                self.finish_apc(p);
                self.state = State::Ground;
                if b != b'\\' {
                    self.step(b, p);
                }
            }

            State::DcsString => {
                if b == 0x07 {
                    self.dispatch_dcs(p);
                    self.state = State::Ground;
                } else if self.dcs.len() < 8_000_000 {
                    self.dcs.push(b);
                } // cap runaway payloads
            }

            State::DcsEsc => {
                self.dispatch_dcs(p);
                self.state = State::Ground;
                if b != b'\\' {
                    self.step(b, p);
                }
            }

            State::CsiEntry | State::CsiParam => {
                if b.is_ascii_digit() {
                    // C# `unchecked`: _current * 10 + digit wraps on overflow.
                    self.current = self
                        .current
                        .wrapping_mul(10)
                        .wrapping_add((b - b'0') as i32);
                    self.has_current = true;
                    self.state = State::CsiParam;
                } else if b == b';' {
                    if self.try_push_param() {
                        self.state = State::CsiParam;
                    } else {
                        self.discard_csi();
                    }
                } else if (0x3c..=0x3f).contains(&b) {
                    self.csi_prefix = b;
                }
                // private marker < = > ?
                else if (0x40..=0x7e).contains(&b) {
                    self.finish_csi(b, p);
                } else if (0x20..=0x2f).contains(&b) {
                    self.state = State::CsiIntermediate;
                } else if is_control(b) {
                    p.execute(b);
                } else {
                    self.state = State::CsiIgnore;
                }
            }

            State::CsiIntermediate => {
                if (0x40..=0x7e).contains(&b) {
                    self.finish_csi(b, p);
                } else if (0x20..=0x2f).contains(&b) { /* collect intermediates: ignored for now */
                } else {
                    self.state = State::CsiIgnore;
                }
            }

            State::CsiIgnore => {
                if (0x40..=0x7e).contains(&b) {
                    self.state = State::Ground;
                }
            }
        }
    }

    fn ground_byte(&mut self, b: u8, p: &mut impl Performer) {
        if self.utf8_remaining > 0 {
            if (b & 0xC0) == 0x80 {
                self.utf8_accum = (self.utf8_accum << 6) | (b & 0x3F) as i32;
                self.utf8_remaining -= 1;
                if self.utf8_remaining == 0 {
                    self.emit_scalar(self.utf8_accum, p);
                }
                return;
            }
            // Invalid continuation: flush the incomplete sequence, then reprocess b fresh.
            self.utf8_remaining = 0;
            p.print(REPLACEMENT);
        }

        if b < 0x80 {
            if is_control(b) {
                p.execute(b);
            } else {
                p.print(b as u16);
            }
        } else if (b & 0xE0) == 0xC0 {
            self.utf8_remaining = 1;
            self.utf8_accum = (b & 0x1F) as i32;
        } else if (b & 0xF0) == 0xE0 {
            self.utf8_remaining = 2;
            self.utf8_accum = (b & 0x0F) as i32;
        } else if (b & 0xF8) == 0xF0 {
            self.utf8_remaining = 3;
            self.utf8_accum = (b & 0x07) as i32;
        } else {
            p.print(REPLACEMENT);
        } // stray continuation or invalid lead byte
    }

    fn dispatch_osc(&mut self, p: &mut impl Performer) {
        // UTF-8 with U+FFFD replacement — matches Encoding.UTF8.GetString.
        let s = String::from_utf8_lossy(&self.osc);
        let (head, text) = match s.find(';') {
            Some(sep) => (&s[..sep], &s[sep + 1..]),
            None => (&s[..], ""),
        };
        // Matches int.TryParse: optional sign, digits only, no over/underflow.
        if let Ok(command) = head.trim().parse::<i32>() {
            // C# int.TryParse rejects leading '+'? No — it accepts "+5" and whitespace is NOT
            // trimmed by default... see oracle note below: we mirror plain parse of the raw head.
            p.osc_dispatch(command, text);
        }
        self.osc.clear();
    }

    fn finish_osc(&mut self, p: &mut impl Performer) {
        if !self.osc_discarding {
            self.dispatch_osc(p);
        } else {
            self.osc.clear();
        }
        self.osc_discarding = false;
    }

    fn dispatch_apc(&mut self, p: &mut impl Performer) {
        if !self.apc.is_empty() {
            let s = core::mem::take(&mut self.apc);
            p.apc_dispatch(&s);
        }
    }

    fn finish_apc(&mut self, p: &mut impl Performer) {
        if !self.apc_discarding {
            self.dispatch_apc(p);
        } else {
            self.apc.clear();
        }
        self.apc_discarding = false;
        self.apc_payload_bytes = 0;
    }

    fn dispatch_dcs(&mut self, p: &mut impl Performer) {
        if !self.dcs.is_empty() {
            let d = core::mem::take(&mut self.dcs);
            p.dcs_dispatch(&d);
        }
    }

    fn emit_scalar(&mut self, scalar: i32, p: &mut impl Performer) {
        // Raw surrogates / out-of-range -> replacement (overlongs deliberately pass, like C#).
        if (0xD800..=0xDFFF).contains(&scalar) || !(0..=0x10FFFF).contains(&scalar) {
            p.print(REPLACEMENT);
            return;
        }
        if scalar > 0xFFFF {
            // Astral: hand the performer the surrogate pair, matching the char-based contract.
            p.print((0xD800 + ((scalar - 0x10000) >> 10)) as u16);
            p.print((0xDC00 + ((scalar - 0x10000) & 0x3FF)) as u16);
            return;
        }
        p.print(scalar as u16);
    }

    fn flush_incomplete_utf8(&mut self, p: &mut impl Performer) {
        if self.utf8_remaining > 0 {
            self.utf8_remaining = 0;
            p.print(REPLACEMENT);
        }
    }

    fn reset_params(&mut self) {
        self.params.clear();
        self.current = 0;
        self.has_current = false;
        self.csi_prefix = 0;
    }

    fn try_push_param(&mut self) -> bool {
        if self.params.len() >= MAX_CSI_PARAMETERS {
            return false;
        }
        let v = if self.has_current { self.current } else { 0 };
        self.params.push(v);
        self.current = 0;
        self.has_current = false;
        true
    }

    fn try_push_param_if_any(&mut self) -> bool {
        if self.has_current || !self.params.is_empty() {
            return self.try_push_param();
        }
        true
    }

    fn finish_csi(&mut self, final_byte: u8, p: &mut impl Performer) {
        if self.try_push_param_if_any() {
            p.csi_dispatch(final_byte, &self.params, self.csi_prefix);
        } else {
            self.reset_params();
        }
        self.state = State::Ground;
    }

    fn discard_csi(&mut self) {
        // A child can leave a CSI split across arbitrarily many reads. Drop the retained list as
        // soon as it exceeds the bound, then ignore through the final byte (or a restarting ESC).
        self.reset_params();
        self.state = State::CsiIgnore;
    }
}

fn is_control(b: u8) -> bool {
    b < 0x20 || b == 0x7f
}

#[cfg(test)]
mod tests {
    use super::*;

    #[derive(Default)]
    struct Rec(Vec<String>);
    impl Performer for Rec {
        fn print(&mut self, ch: u16) {
            self.0.push(format!("P:{ch:04X}"));
        }
        fn execute(&mut self, b: u8) {
            self.0.push(format!("E:{b:02X}"));
        }
        fn esc_dispatch(&mut self, ch: u8) {
            self.0.push(format!("ESC:{}", ch as char));
        }
        fn csi_dispatch(&mut self, ch: u8, params: &[i32], prefix: u8) {
            let ps: Vec<String> = params.iter().map(|p| p.to_string()).collect();
            self.0
                .push(format!("CSI:{}:{}:{}", ch as char, prefix, ps.join(",")));
        }
        fn osc_dispatch(&mut self, command: i32, text: &str) {
            self.0.push(format!("OSC:{command}:{text}"));
        }
        fn apc_dispatch(&mut self, text: &str) {
            self.0.push(format!("APC:{text}"));
        }
        fn dcs_dispatch(&mut self, payload: &[u8]) {
            self.0.push(format!("DCS:{}", payload.len()));
        }
    }

    fn run(bytes: &[u8]) -> Vec<String> {
        let mut p = VtParser::new();
        let mut r = Rec::default();
        p.feed(bytes, &mut r);
        r.0
    }

    #[test]
    fn sgr_and_text() {
        let ev = run(b"\x1b[1;31mA");
        assert_eq!(ev, vec!["CSI:m:0:1,31", "P:0041"]); // prefix 0 = no private marker
    }

    #[test]
    fn osc_bel_and_st() {
        assert_eq!(run(b"\x1b]0;title\x07"), vec!["OSC:0:title"]);
        assert_eq!(run(b"\x1b]7;file://x\x1b\\"), vec!["OSC:7:file://x"]);
    }

    #[test]
    fn astral_prints_surrogate_pair() {
        let ev = run("🚀".as_bytes());
        assert_eq!(ev, vec!["P:D83D", "P:DE80"]);
    }

    #[test]
    fn private_mode_and_controls_mid_csi() {
        assert_eq!(run(b"\x1b[?25l"), vec![format!("CSI:l:{}:25", b'?')]);
        assert_eq!(
            run(b"\x1b[1;\r2H"),
            vec!["E:0D".to_string(), format!("CSI:H:{}:1,2", 0)]
        );
    }

    #[test]
    fn oversized_osc_is_discarded_and_parser_recovers() {
        let mut parser = VtParser::new();
        let mut rec = Rec::default();
        parser.feed(b"\x1b]0;", &mut rec);
        parser.feed(&vec![0; MAX_STRING_PAYLOAD_BYTES + 1], &mut rec);
        parser.feed(b"\x07X\x1b]0;ok\x07", &mut rec);

        assert_eq!(rec.0, vec!["P:0058", "OSC:0:ok"]);
    }

    #[test]
    fn oversized_apc_is_discarded_and_parser_recovers() {
        let mut parser = VtParser::new();
        let mut rec = Rec::default();
        parser.feed(b"\x1b_", &mut rec);
        parser.feed(&vec![0; MAX_STRING_PAYLOAD_BYTES + 1], &mut rec);
        parser.feed(b"\x1b\\X\x1b_Gok\x1b\\", &mut rec);

        assert_eq!(rec.0, vec!["P:0058", "APC:Gok"]);
    }

    #[test]
    fn oversized_csi_parameter_list_is_discarded_across_chunks_and_parser_recovers() {
        let mut parser = VtParser::new();
        let mut rec = Rec::default();
        parser.feed(b"\x1b[", &mut rec);
        parser.feed(&vec![b';'; MAX_CSI_PARAMETERS], &mut rec);
        parser.feed(b";", &mut rec); // overflow arrives in a later terminal read
        parser.feed(b"mX\x1b[31m", &mut rec);

        assert_eq!(rec.0, vec!["P:0058", "CSI:m:0:31"]);
    }
}
