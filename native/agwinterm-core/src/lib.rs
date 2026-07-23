//! agwinterm-core: Rust port of the Agwinterm.Core terminal emulator
//! (strategy decided 2026-07: leak audit first, then this incremental port).
//!
//! Port order (each stage validated against the C# implementation as a
//! differential oracle before the next begins):
//!   1. wcwidth        — DONE
//!   2. cell + screen buffer (grid, scrollback, BCE)
//!   3. VT parser (CSI/OSC/DCS state machine)
//!   4. emulator (cursor, modes, SGR, scroll regions, alt screen, marks)
//!   5. sixel/kitty image decode
//!
//! The C ABI below is the only public surface the C# side sees. It grows
//! stage by stage; nothing is exported until its differential tests pass.

pub mod wcwidth;

/// Bumped whenever the exported C surface changes shape. The C# loader
/// refuses a mismatch loudly (same hard-handshake philosophy as the
/// pty-host protocol).
pub const ABI_VERSION: u32 = 1;

#[unsafe(no_mangle)]
pub extern "C" fn agwcore_abi_version() -> u32 {
    ABI_VERSION
}

/// East-Asian display width of a codepoint: 0, 1, or 2.
/// Mirrors Agwinterm.Core.Wcwidth.Of — the differential test sweeps every
/// codepoint through both and asserts equality.
#[unsafe(no_mangle)]
pub extern "C" fn agwcore_wcwidth(codepoint: u32) -> u8 {
    wcwidth::of(codepoint)
}
