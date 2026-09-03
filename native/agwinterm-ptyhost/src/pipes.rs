//! Named-pipe servers, blocking threads only — no IOCP, no overlapped teardown
//! races (the #118 crash class is structurally absent here).

use std::fs::File;
use std::os::windows::io::FromRawHandle;
use std::ptr::{null, null_mut};

use windows_sys::Win32::Foundation::{
    CloseHandle, ERROR_PIPE_CONNECTED, GetLastError, HANDLE, INVALID_HANDLE_VALUE,
};
use windows_sys::Win32::System::Pipes::{
    ConnectNamedPipe, CreateNamedPipeW, PIPE_READMODE_BYTE, PIPE_TYPE_BYTE,
    PIPE_UNLIMITED_INSTANCES, PIPE_WAIT,
};

const PIPE_ACCESS_DUPLEX: u32 = 0x0000_0003;

fn wide(s: &str) -> Vec<u16> {
    s.encode_utf16().chain(std::iter::once(0)).collect()
}

/// One pipe-server instance. Create → (blocking) connect → File for read/write.
pub struct PipeServer {
    handle: HANDLE,
}

unsafe impl Send for PipeServer {}

impl PipeServer {
    pub fn create(name: &str) -> Result<PipeServer, String> {
        let full = wide(&format!(r"\\.\pipe\{name}"));
        let h = unsafe {
            CreateNamedPipeW(
                full.as_ptr(),
                PIPE_ACCESS_DUPLEX,
                PIPE_TYPE_BYTE | PIPE_READMODE_BYTE | PIPE_WAIT,
                PIPE_UNLIMITED_INSTANCES,
                64 * 1024,
                64 * 1024,
                0,
                null(),
            )
        };
        if h == INVALID_HANDLE_VALUE {
            return Err(format!(
                "CreateNamedPipeW({name}) failed: {}",
                std::io::Error::last_os_error()
            ));
        }
        Ok(PipeServer { handle: h })
    }

    /// Block until a client connects; returns the duplex stream. Consumes self —
    /// closing the returned File closes the instance.
    pub fn accept(self) -> Result<File, String> {
        let ok = unsafe { ConnectNamedPipe(self.handle, null_mut()) };
        if ok == 0 && unsafe { GetLastError() } != ERROR_PIPE_CONNECTED {
            let err = std::io::Error::last_os_error();
            unsafe { CloseHandle(self.handle) };
            return Err(format!("ConnectNamedPipe failed: {err}"));
        }
        let h = self.handle;
        std::mem::forget(self); // File owns the handle now
        Ok(unsafe { File::from_raw_handle(h as *mut _) })
    }
}

impl Drop for PipeServer {
    fn drop(&mut self) {
        unsafe { CloseHandle(self.handle) };
    }
}

// ---- Overlapped duplex pipe (DATA pipes) ----
// A non-overlapped duplex instance SERIALIZES the handle: a pending blocking read
// blocks writes from other threads forever — exactly the host's data-pipe shape
// (input pump reads while the output forwarder writes). So data pipes are created
// FILE_FLAG_OVERLAPPED and driven blocking-style with per-call events; CancelIoEx
// gives the host a way to end a pending read (detach/supersede/kill).

use windows_sys::Win32::Foundation::{ERROR_IO_PENDING, FALSE};
use windows_sys::Win32::Storage::FileSystem::{FILE_FLAG_OVERLAPPED, ReadFile, WriteFile};
use windows_sys::Win32::System::IO::{CancelIoEx, GetOverlappedResult, OVERLAPPED};
use windows_sys::Win32::System::Threading::{CreateEventW, INFINITE, WaitForSingleObject};

pub struct OverlappedPipeServer {
    handle: HANDLE,
}

unsafe impl Send for OverlappedPipeServer {}

pub struct OvStream {
    handle: HANDLE,
}

unsafe impl Send for OvStream {}
unsafe impl Sync for OvStream {}

struct Event(HANDLE);
impl Event {
    fn new() -> Event {
        Event(unsafe { CreateEventW(null(), 1, 0, null()) })
    }
}
impl Drop for Event {
    fn drop(&mut self) {
        unsafe { CloseHandle(self.0) };
    }
}

/// Run one overlapped op blocking-style: issue, wait on the event, collect the result.
unsafe fn finish(handle: HANDLE, ov: &mut OVERLAPPED, issued: i32) -> Option<u32> {
    if issued == 0 && unsafe { GetLastError() } != ERROR_IO_PENDING {
        return None;
    }
    let mut n = 0u32;
    if unsafe { GetOverlappedResult(handle, ov, &mut n, 1) } == FALSE {
        return None;
    }
    Some(n)
}

impl OverlappedPipeServer {
    pub fn create(name: &str) -> Result<OverlappedPipeServer, String> {
        let full = wide(&format!(r"\\.\pipe\{name}"));
        let h = unsafe {
            CreateNamedPipeW(
                full.as_ptr(),
                PIPE_ACCESS_DUPLEX | FILE_FLAG_OVERLAPPED,
                PIPE_TYPE_BYTE | PIPE_READMODE_BYTE | PIPE_WAIT,
                1,
                64 * 1024,
                64 * 1024,
                0,
                null(),
            )
        };
        if h == INVALID_HANDLE_VALUE {
            return Err(format!(
                "CreateNamedPipeW({name}) failed: {}",
                std::io::Error::last_os_error()
            ));
        }
        Ok(OverlappedPipeServer { handle: h })
    }

    /// Block until a client connects (overlapped connect + event wait).
    pub fn accept(self) -> Result<OvStream, String> {
        unsafe {
            let ev = Event::new();
            let mut ov: OVERLAPPED = std::mem::zeroed();
            ov.hEvent = ev.0;
            let issued = ConnectNamedPipe(self.handle, &mut ov);
            if issued == 0 {
                let err = GetLastError();
                if err != ERROR_PIPE_CONNECTED && err != ERROR_IO_PENDING {
                    return Err(format!("ConnectNamedPipe failed: {err}"));
                }
                if err == ERROR_IO_PENDING {
                    WaitForSingleObject(ev.0, INFINITE);
                }
            }
            let h = self.handle;
            std::mem::forget(self);
            Ok(OvStream { handle: h })
        }
    }
}

impl Drop for OverlappedPipeServer {
    fn drop(&mut self) {
        unsafe { CloseHandle(self.handle) };
    }
}

impl OvStream {
    pub fn read(&self, buf: &mut [u8]) -> usize {
        unsafe {
            let ev = Event::new();
            let mut ov: OVERLAPPED = std::mem::zeroed();
            ov.hEvent = ev.0;
            let issued = ReadFile(
                self.handle,
                buf.as_mut_ptr(),
                buf.len() as u32,
                null_mut(),
                &mut ov,
            );
            finish(self.handle, &mut ov, issued).unwrap_or(0) as usize
        }
    }

    pub fn write_all(&self, mut buf: &[u8]) -> bool {
        unsafe {
            while !buf.is_empty() {
                let ev = Event::new();
                let mut ov: OVERLAPPED = std::mem::zeroed();
                ov.hEvent = ev.0;
                let issued = WriteFile(
                    self.handle,
                    buf.as_ptr(),
                    buf.len() as u32,
                    null_mut(),
                    &mut ov,
                );
                let Some(n) = finish(self.handle, &mut ov, issued) else {
                    return false;
                };
                if n == 0 {
                    return false;
                }
                buf = &buf[n as usize..];
            }
            true
        }
    }

    /// Wake any pending read/write with ERROR_OPERATION_ABORTED (host-initiated detach).
    pub fn cancel_io(&self) {
        unsafe { CancelIoEx(self.handle, null()) };
    }
}

impl Drop for OvStream {
    fn drop(&mut self) {
        unsafe { CloseHandle(self.handle) };
    }
}
