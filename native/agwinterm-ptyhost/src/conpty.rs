//! ConPTY session: CreatePseudoConsole + CreateProcessW with the pseudoconsole
//! attribute — the Rust equivalent of what Porta.Pty does for the C# host.

use std::ffi::c_void;
use std::fs::File;
use std::io::{Read, Write};
use std::os::windows::io::FromRawHandle;
use std::ptr::{null, null_mut};

use windows_sys::Win32::Foundation::{CloseHandle, HANDLE, S_OK, WAIT_OBJECT_0};
use windows_sys::Win32::System::Console::{
    ClosePseudoConsole, CreatePseudoConsole, ResizePseudoConsole, COORD, HPCON,
};
use windows_sys::Win32::System::Pipes::CreatePipe;
use windows_sys::Win32::System::Threading::{
    CreateProcessW, DeleteProcThreadAttributeList, GetExitCodeProcess,
    InitializeProcThreadAttributeList, UpdateProcThreadAttribute, WaitForSingleObject,
    EXTENDED_STARTUPINFO_PRESENT, INFINITE, LPPROC_THREAD_ATTRIBUTE_LIST, PROCESS_INFORMATION,
    STARTUPINFOEXW, STARTUPINFOW,
};

const PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE: usize = 0x00020016;

pub struct ConPty {
    hpc: HPCON,
    pub child: HANDLE,
    pub child_pid: u32,
    /// Read side of ConPTY output (the VT byte stream).
    pub output: File,
    /// Write side of ConPTY input (keystrokes).
    input_write: File,
    cols: i16,
    rows: i16,
}

// Handles are used from multiple threads under our own locking discipline.
unsafe impl Send for ConPty {}

fn wide(s: &str) -> Vec<u16> {
    s.encode_utf16().chain(std::iter::once(0)).collect()
}

/// Quote one argument per CommandLineToArgvW rules (port of TerminalSession.QuoteArg).
pub fn quote_arg(arg: &str) -> String {
    if !arg.is_empty() && !arg.chars().any(|c| matches!(c, ' ' | '\t' | '\n' | '\x0b' | '"')) {
        return arg.to_string();
    }
    let mut out = String::from("\"");
    let chars: Vec<char> = arg.chars().collect();
    let mut i = 0;
    loop {
        let mut slashes = 0;
        while i < chars.len() && chars[i] == '\\' {
            i += 1;
            slashes += 1;
        }
        if i == chars.len() {
            out.extend(std::iter::repeat('\\').take(slashes * 2));
            break;
        }
        if chars[i] == '"' {
            out.extend(std::iter::repeat('\\').take(slashes * 2 + 1));
            out.push('"');
        } else {
            out.extend(std::iter::repeat('\\').take(slashes));
            out.push(chars[i]);
        }
        i += 1;
    }
    out.push('"');
    out
}

/// UTF-16 double-null environment block from key=value pairs (sorted, Windows convention).
fn env_block(vars: &[(String, String)]) -> Vec<u16> {
    let mut sorted: Vec<&(String, String)> = vars.iter().collect();
    sorted.sort_by(|a, b| a.0.to_uppercase().cmp(&b.0.to_uppercase()));
    let mut block: Vec<u16> = Vec::new();
    for (k, v) in sorted {
        block.extend(format!("{k}={v}").encode_utf16());
        block.push(0);
    }
    block.push(0);
    block
}

impl ConPty {
    pub fn spawn(
        app: &str,
        args: &[String],
        verbatim: bool,
        cwd: Option<&str>,
        env: Option<&[(String, String)]>,
        cols: i16,
        rows: i16,
    ) -> Result<ConPty, String> {
        unsafe {
            let (mut in_read, mut in_write): (HANDLE, HANDLE) = (null_mut(), null_mut());
            let (mut out_read, mut out_write): (HANDLE, HANDLE) = (null_mut(), null_mut());
            if CreatePipe(&mut in_read, &mut in_write, null(), 0) == 0
                || CreatePipe(&mut out_read, &mut out_write, null(), 0) == 0
            {
                return Err("CreatePipe failed".into());
            }

            let mut hpc: HPCON = 0;
            let size = COORD { X: cols, Y: rows };
            let hr = CreatePseudoConsole(size, in_read, out_write, 0, &mut hpc);
            if hr != S_OK {
                return Err(format!("CreatePseudoConsole failed: 0x{hr:08x}"));
            }
            // ConPTY duplicated these; our copies would keep the pipes alive past child exit.
            CloseHandle(in_read);
            CloseHandle(out_write);

            // Attribute list with the pseudoconsole.
            let mut attr_size: usize = 0;
            InitializeProcThreadAttributeList(null_mut(), 1, 0, &mut attr_size);
            let mut attr_buf = vec![0u8; attr_size];
            let attr_list = attr_buf.as_mut_ptr() as LPPROC_THREAD_ATTRIBUTE_LIST;
            if InitializeProcThreadAttributeList(attr_list, 1, 0, &mut attr_size) == 0 {
                ClosePseudoConsole(hpc);
                return Err("InitializeProcThreadAttributeList failed".into());
            }
            if UpdateProcThreadAttribute(
                attr_list,
                0,
                PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE,
                hpc as *const c_void,
                size_of::<HPCON>(),
                null_mut(),
                null(),
            ) == 0
            {
                DeleteProcThreadAttributeList(attr_list);
                ClosePseudoConsole(hpc);
                return Err("UpdateProcThreadAttribute failed".into());
            }

            let cmdline = if args.is_empty() {
                quote_arg(app)
            } else if verbatim {
                format!("{} {}", quote_arg(app), args.join(" "))
            } else {
                let quoted: Vec<String> = args.iter().map(|a| quote_arg(a)).collect();
                format!("{} {}", quote_arg(app), quoted.join(" "))
            };
            let mut cmdline_w = wide(&cmdline);
            let cwd_w = cwd.map(wide);
            let env_w = env.map(env_block);

            let mut si: STARTUPINFOEXW = std::mem::zeroed();
            si.StartupInfo.cb = size_of::<STARTUPINFOEXW>() as u32;
            si.lpAttributeList = attr_list;
            let mut pi: PROCESS_INFORMATION = std::mem::zeroed();

            const CREATE_UNICODE_ENVIRONMENT: u32 = 0x0400;
            let ok = CreateProcessW(
                null(),
                cmdline_w.as_mut_ptr(),
                null(),
                null(),
                0,
                EXTENDED_STARTUPINFO_PRESENT | CREATE_UNICODE_ENVIRONMENT,
                env_w.as_ref().map_or(null_mut(), |e| e.as_ptr() as *mut c_void),
                cwd_w.as_ref().map_or(null(), |c| c.as_ptr()),
                &si as *const STARTUPINFOEXW as *const STARTUPINFOW,
                &mut pi,
            );
            DeleteProcThreadAttributeList(attr_list);
            if ok == 0 {
                let err = std::io::Error::last_os_error();
                ClosePseudoConsole(hpc);
                CloseHandle(in_write);
                CloseHandle(out_read);
                return Err(format!(
                    "Could not start terminal process {cmdline}: {}",
                    err
                ));
            }
            CloseHandle(pi.hThread);

            Ok(ConPty {
                hpc,
                child: pi.hProcess,
                child_pid: pi.dwProcessId,
                output: File::from_raw_handle(out_read as *mut _),
                input_write: File::from_raw_handle(in_write as *mut _),
                cols,
                rows,
            })
        }
    }

    pub fn write_input(&self, bytes: &[u8]) -> bool {
        // &File implements Write (WriteFile is thread-safe on pipe handles).
        let mut w = &self.input_write;
        w.write_all(bytes).is_ok() && w.flush().is_ok()
    }

    pub fn resize(&mut self, cols: i16, rows: i16) {
        if cols > 0 && rows > 0 {
            unsafe { ResizePseudoConsole(self.hpc, COORD { X: cols, Y: rows }) };
            self.cols = cols;
            self.rows = rows;
        }
    }

    pub fn size(&self) -> (i16, i16) {
        (self.cols, self.rows)
    }

    /// Blocking wait for child exit; returns the exit code.
    pub fn wait_exit(&self) -> i32 {
        unsafe {
            if WaitForSingleObject(self.child, INFINITE) == WAIT_OBJECT_0 {
                let mut code = 0u32;
                GetExitCodeProcess(self.child, &mut code);
                return code as i32;
            }
        }
        -1
    }

    pub fn kill(&self) {
        unsafe {
            windows_sys::Win32::System::Threading::TerminateProcess(self.child, 1);
        }
    }

    /// Reader over ConPTY output (call from a dedicated pump thread).
    pub fn read_output(&mut self, buf: &mut [u8]) -> usize {
        self.output.read(buf).unwrap_or(0)
    }
}

impl Drop for ConPty {
    fn drop(&mut self) {
        unsafe {
            // Close the pseudoconsole FIRST: without it the output pipe never EOFs
            // (same lesson as TerminalSession.Dispose).
            ClosePseudoConsole(self.hpc);
            CloseHandle(self.child);
        }
        let _ = self.output.flush();
    }
}


/// Blocking wait on a raw child handle (usize-cast) + exit code. Safe to run
/// concurrently with pty-mutex users; a closed handle yields -1.
pub fn wait_child(child: usize) -> i32 {
    unsafe {
        let h = child as HANDLE;
        if WaitForSingleObject(h, INFINITE) == WAIT_OBJECT_0 {
            let mut code = 0u32;
            GetExitCodeProcess(h, &mut code);
            return code as i32;
        }
        -1
    }
}