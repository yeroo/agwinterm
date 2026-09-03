//! Port of Agwinterm.Pty/FreshEnvironment.cs: the environment a genuinely fresh
//! process tree would get (userenv re-reads registry machine+user vars, composes
//! PATH, expands REG_EXPAND_SZ). Fallback: this process's inherited environment.

use std::ptr::null_mut;

use windows_sys::Win32::Foundation::{CloseHandle, HANDLE};
use windows_sys::Win32::System::Environment::{CreateEnvironmentBlock, DestroyEnvironmentBlock};
use windows_sys::Win32::System::Threading::{GetCurrentProcess, OpenProcessToken};

const TOKEN_QUERY: u32 = 0x0008;
const TOKEN_DUPLICATE: u32 = 0x0002;

pub fn fresh_environment() -> Vec<(String, String)> {
    if let Some(vars) = try_registry_fresh()
        && !vars.is_empty()
    {
        return vars;
    }
    std::env::vars().collect() // pre-feature behavior: inherit
}

fn try_registry_fresh() -> Option<Vec<(String, String)>> {
    unsafe {
        let mut token: HANDLE = null_mut();
        if OpenProcessToken(
            GetCurrentProcess(),
            TOKEN_QUERY | TOKEN_DUPLICATE,
            &mut token,
        ) == 0
        {
            return None;
        }
        let mut block: *mut core::ffi::c_void = null_mut();
        let ok = CreateEnvironmentBlock(&mut block, token, 0);
        CloseHandle(token);
        if ok == 0 || block.is_null() {
            return None;
        }
        // Double-NUL-terminated run of "NAME=value" UTF-16 strings.
        let mut vars = Vec::new();
        let mut p = block as *const u16;
        loop {
            let mut len = 0usize;
            while *p.add(len) != 0 {
                len += 1;
            }
            if len == 0 {
                break;
            }
            let entry = String::from_utf16_lossy(core::slice::from_raw_parts(p, len));
            if let Some(eq) = entry.find('=')
                && eq > 0
            {
                vars.push((entry[..eq].to_string(), entry[eq + 1..].to_string()));
            }
            p = p.add(len + 1);
        }
        DestroyEnvironmentBlock(block);
        Some(vars)
    }
}
