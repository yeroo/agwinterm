//! agwinterm-ptyhost: the pty-host in Rust. Speaks EXACTLY the protocol of the C#
//! PtyHostServer (hello/create/attach/detach/resize/kill/list/shutdown over a
//! newline-JSON control pipe; per-attach duplex data pipes) — the same C#
//! PtyHostClient drives both, which is also how the compatibility tests work.
//!
//! Design: blocking threads everywhere (accept loop, per-client control loop,
//! per-session output pump, per-attach input pump). No async, no IOCP.
//!
//! v1 limitations vs the C# host (loud errors, not silent gaps):
//!  - deElevate is refused ("de-elevate unsupported by rust host").

mod conpty;
mod freshenv;
mod pipes;

use std::collections::HashMap;
use std::fs::File;
use std::io::{BufRead, BufReader, Read, Write};
use std::sync::atomic::{AtomicBool, AtomicI32, AtomicU64, Ordering};
use std::sync::{Arc, Mutex};

use agwinterm_core::emulator::Terminal;
use conpty::ConPty;
use pipes::{OverlappedPipeServer, OvStream, PipeServer};
use serde_json::{json, Value};

const PROTOCOL_VERSION: i64 = 1;

struct Hosted {
    id: String,
    pty: Mutex<ConPty>,
    term: Mutex<Terminal>,
    /// Write side handed to the output forwarder; replaced on supersede, dropped on detach.
    data: Mutex<Option<Arc<OvStream>>>,
    exited: AtomicBool,
    exit_code: AtomicI32,
}

struct Host {
    app_id: String,
    sessions: Mutex<HashMap<String, Arc<Hosted>>>,
    attach_seq: AtomicU64,
}

fn main() {
    let mut pipe = None;
    let args: Vec<String> = std::env::args().collect();
    let mut i = 1;
    while i < args.len() {
        if args[i] == "--pipe" && i + 1 < args.len() {
            pipe = Some(args[i + 1].clone());
            i += 1;
        }
        i += 1;
    }
    let app_id = pipe.unwrap_or_else(|| "agwinterm".to_string());
    let host = Arc::new(Host {
        app_id: app_id.clone(),
        sessions: Mutex::new(HashMap::new()),
        attach_seq: AtomicU64::new(0),
    });

    let control = format!("{app_id}-ptyhost");
    loop {
        let server = match PipeServer::create(&control) {
            Ok(s) => s,
            Err(e) => {
                eprintln!("{e}");
                std::process::exit(1);
            }
        };
        let stream = match server.accept() {
            Ok(f) => f,
            Err(_) => continue,
        };
        let host = host.clone();
        std::thread::spawn(move || handle_control_client(host, stream));
    }
}

fn handle_control_client(host: Arc<Host>, stream: File) {
    let mut writer = match stream.try_clone() {
        Ok(w) => w,
        Err(_) => return,
    };
    let reader = BufReader::new(stream);
    for line in reader.lines() {
        let Ok(line) = line else { return };
        if line.is_empty() {
            continue;
        }
        let reply = dispatch(&host, &line);
        if writer.write_all(reply.as_bytes()).is_err() || writer.write_all(b"\n").is_err() {
            return;
        }
        let _ = writer.flush();
        if reply.contains("\"__shutdown\":true") {
            // Ack flushed; tear down after a beat (same 100ms grace as the C# host).
            std::thread::sleep(std::time::Duration::from_millis(100));
            let sessions: Vec<Arc<Hosted>> = host.sessions.lock().unwrap().values().cloned().collect();
            for s in sessions {
                s.pty.lock().unwrap().kill();
            }
            std::process::exit(0);
        }
    }
}

fn ok(mut body: serde_json::Map<String, Value>) -> String {
    body.insert("ok".into(), Value::Bool(true));
    // Serialize with "ok" present; field order is irrelevant to the client (JSON object).
    Value::Object(body).to_string()
}

fn err(msg: &str) -> String {
    json!({ "ok": false, "error": msg }).to_string()
}

fn dispatch(host: &Arc<Host>, line: &str) -> String {
    let Ok(root) = serde_json::from_str::<Value>(line) else {
        return err("invalid JSON");
    };
    let cmd = root.get("cmd").and_then(Value::as_str).unwrap_or("");
    match cmd {
        "hello" => {
            let theirs = root.get("protocol").and_then(Value::as_i64).unwrap_or(-1);
            if theirs == PROTOCOL_VERSION {
                let mut m = serde_json::Map::new();
                m.insert("protocol".into(), json!(PROTOCOL_VERSION));
                m.insert("pid".into(), json!(std::process::id()));
                ok(m)
            } else {
                err(&format!("protocol mismatch: host={PROTOCOL_VERSION} client={theirs}"))
            }
        }
        "create" => handle_create(host, &root),
        "attach" => handle_attach(host, &root),
        "detach" => with_session(host, &root, |h| {
            detach(&h); // cancel + drop = client EOF
            ok(serde_json::Map::new())
        }),
        "resize" => with_session(host, &root, |h| {
            let cols = root.get("cols").and_then(Value::as_i64).unwrap_or(0);
            let rows = root.get("rows").and_then(Value::as_i64).unwrap_or(0);
            if cols <= 0 || rows <= 0 {
                return err("resize needs cols/rows");
            }
            h.term.lock().unwrap().emu.resize(cols as usize, rows as usize);
            h.pty.lock().unwrap().resize(cols as i16, rows as i16);
            ok(serde_json::Map::new())
        }),
        "kill" => {
            let Some(h) = take_session(host, &root) else { return err_no_session(&root) };
            detach(&h);
            h.pty.lock().unwrap().kill();
            ok(serde_json::Map::new())
        }
        "list" => {
            let sessions = host.sessions.lock().unwrap();
            let mut arr = Vec::new();
            for h in sessions.values() {
                let (cols, rows) = h.pty.lock().unwrap().size();
                let title = h.term.lock().unwrap().emu.title.clone();
                arr.push(json!({
                    "id": h.id,
                    "cols": cols, "rows": rows,
                    "childPid": h.pty.lock().unwrap().child_pid,
                    "hasExited": h.exited.load(Ordering::SeqCst),
                    "exitCode": h.exit_code.load(Ordering::SeqCst),
                    "title": title,
                    "attached": h.data.lock().unwrap().is_some(),
                }));
            }
            let mut m = serde_json::Map::new();
            m.insert("sessions".into(), Value::Array(arr));
            ok(m)
        }
        "shutdown" => {
            let mut m = serde_json::Map::new();
            m.insert("__shutdown".into(), Value::Bool(true)); // control loop exits after flushing
            ok(m)
        }
        _ => err(&format!("unknown command '{cmd}'")),
    }
}

fn err_no_session(root: &Value) -> String {
    err(&format!("no session '{}'", root.get("id").and_then(Value::as_str).unwrap_or("")))
}

fn with_session(host: &Arc<Host>, root: &Value, act: impl FnOnce(&Arc<Hosted>) -> String) -> String {
    let Some(id) = root.get("id").and_then(Value::as_str) else { return err("missing id") };
    let h = host.sessions.lock().unwrap().get(id).cloned();
    match h {
        Some(h) => act(&h),
        None => err(&format!("no session '{id}'")),
    }
}

fn take_session(host: &Arc<Host>, root: &Value) -> Option<Arc<Hosted>> {
    let id = root.get("id").and_then(Value::as_str)?;
    host.sessions.lock().unwrap().remove(id)
}

fn detach(h: &Arc<Hosted>) {
    let mut data = h.data.lock().unwrap();
    if let Some(d) = data.take() {
        d.cancel_io(); // wake the input pump; last Arc drop closes the handle = client EOF
    }
}

fn handle_create(host: &Arc<Host>, root: &Value) -> String {
    let id = root.get("id").and_then(Value::as_str).unwrap_or("").to_string();
    if id.is_empty() {
        return err("create needs args.id");
    }
    let cols = root.get("cols").and_then(Value::as_i64).unwrap_or(120).clamp(1, 10000);
    let rows = root.get("rows").and_then(Value::as_i64).unwrap_or(30).clamp(1, 10000);
    let app = root.get("app").and_then(Value::as_str).unwrap_or("");
    if app.is_empty() {
        return err("create needs args.app");
    }
    let args: Vec<String> = root
        .get("args")
        .and_then(Value::as_array)
        .map(|a| a.iter().filter_map(Value::as_str).map(String::from).collect())
        .unwrap_or_default();
    let cwd = root.get("cwd").and_then(Value::as_str).map(String::from);
    let verbatim = root.get("verbatim").and_then(Value::as_bool).unwrap_or(false);
    let de_elevate = root.get("deElevate").and_then(Value::as_bool).unwrap_or(false);
    let fresh_env = root.get("freshEnv").and_then(Value::as_bool).unwrap_or(true);
    if de_elevate {
        return err("spawn failed: de-elevate unsupported by rust host");
    }
    let extra: Vec<(String, String)> = root
        .get("env")
        .and_then(Value::as_object)
        .map(|o| o.iter().filter_map(|(k, v)| v.as_str().map(|s| (k.clone(), s.to_string()))).collect())
        .unwrap_or_default();

    {
        let sessions = host.sessions.lock().unwrap();
        if sessions.contains_key(&id) {
            return err(&format!("session '{id}' already exists"));
        }
    }

    // Base env: registry-fresh (or inherited) + our additions — same as TerminalSession.
    let env: Option<Vec<(String, String)>> = if fresh_env || !extra.is_empty() {
        let mut base = if fresh_env { freshenv::fresh_environment() } else { std::env::vars().collect() };
        for (k, v) in &extra {
            if let Some(slot) = base.iter_mut().find(|(bk, _)| bk.eq_ignore_ascii_case(k)) {
                slot.1 = v.clone();
            } else {
                base.push((k.clone(), v.clone()));
            }
        }
        Some(base)
    } else {
        None
    };

    let pty = match ConPty::spawn(app, &args, verbatim, cwd.as_deref(), env.as_deref(), cols as i16, rows as i16) {
        Ok(p) => p,
        Err(e) => return err(&format!("spawn failed: {e}")),
    };

    let hosted = Arc::new(Hosted {
        id: id.clone(),
        term: Mutex::new(Terminal::new(cols as usize, rows as usize)),
        pty: Mutex::new(pty),
        data: Mutex::new(None),
        exited: AtomicBool::new(false),
        exit_code: AtomicI32::new(0),
    });
    host.sessions.lock().unwrap().insert(id.clone(), hosted.clone());
    let hosted2 = hosted.clone();

    // Output pump: ConPTY → emulator (+ forward raw to the attached client).
    std::thread::spawn(move || {
        // A separate read handle: File::try_clone on the pipe read end.
        let mut out = { hosted.pty.lock().unwrap().output.try_clone() };
        let Ok(ref mut out) = out else { return };
        let mut buf = [0u8; 64 * 1024];
        loop {
            let n = out.read(&mut buf).unwrap_or(0);
            if n == 0 {
                break; // ConPTY closed → child gone
            }
            hosted.term.lock().unwrap().feed(&buf[..n]);
            let mut data = hosted.data.lock().unwrap();
            if let Some(d) = data.as_ref() {
                if !d.write_all(&buf[..n]) {
                    *data = None; // client vanished mid-write -> plain detach
                }
            }
        }
    });

    // Exit watcher on the raw child handle — ConPTY's output pipe does NOT EOF when the
    // child exits (conhost keeps it open), so the pump can't be the exit signal. Waiting
    // via the pty mutex would hold it for the child's whole lifetime; the raw handle is
    // safe to wait on concurrently.
    let hw = hosted2;
    let child_h = hw.pty.lock().unwrap().child as usize;
    std::thread::spawn(move || {
        let code = conpty::wait_child(child_h);
        hw.exit_code.store(code, Ordering::SeqCst);
        hw.exited.store(true, Ordering::SeqCst);
        detach(&hw); // data-pipe EOF = the client's exit signal
    });

    let mut m = serde_json::Map::new();
    m.insert("id".into(), Value::String(id));
    ok(m)
}

fn handle_attach(host: &Arc<Host>, root: &Value) -> String {
    let Some(id) = root.get("id").and_then(Value::as_str) else { return err("missing id") };
    let Some(hosted) = host.sessions.lock().unwrap().get(id).cloned() else {
        return err(&format!("no session '{id}'"));
    };
    let repaint = root.get("repaint").and_then(Value::as_bool).unwrap_or(false);
    let seq = host.attach_seq.fetch_add(1, Ordering::SeqCst);
    let data_name = format!("{}-ptyhost-d-{seq:08x}", host.app_id);
    let server = match OverlappedPipeServer::create(&data_name) {
        Ok(s) => s,
        Err(e) => return err(&e),
    };

    // Snapshot under the emulator lock, before any new output can race the seed.
    let (scrollback, modes, cols, rows) = {
        let t = hosted.term.lock().unwrap();
        let mut lines = Vec::with_capacity(t.emu.history_count());
        for h in 0..t.emu.history_count() {
            lines.push(dump_history_row(&t, h));
        }
        let (c, r) = (t.emu.screen().cols(), t.emu.screen().rows());
        (lines, t.emu.dump_modes(), c, r)
    };

    let h2 = hosted.clone();
    std::thread::spawn(move || {
        let Ok(stream) = server.accept() else { return };
        let stream = Arc::new(stream);
        {
            // Supersede: cancel + drop the previous attachment (its client EOFs).
            let mut data = h2.data.lock().unwrap();
            if let Some(old) = data.take() {
                old.cancel_io();
            }
            *data = Some(stream.clone());
        }
        if h2.exited.load(Ordering::SeqCst) {
            *h2.data.lock().unwrap() = None; // exited while attaching → immediate EOF
            return;
        }
        if repaint {
            // The ConPTY repaint jiggle: a real row-count change makes conhost re-emit the viewport.
            let (c, r) = h2.pty.lock().unwrap().size();
            h2.term.lock().unwrap().emu.resize(c as usize, (r as usize).saturating_sub(1).max(2));
            h2.pty.lock().unwrap().resize(c, (r - 1).max(2));
            std::thread::sleep(std::time::Duration::from_millis(60));
            h2.term.lock().unwrap().emu.resize(c as usize, r as usize);
            h2.pty.lock().unwrap().resize(c, r);
        }
        // Input pump: client bytes → child stdin. EOF/error/cancel = detach (session keeps running).
        let mut buf = [0u8; 16 * 1024];
        loop {
            let n = stream.read(&mut buf);
            if n == 0 {
                break;
            }
            if !h2.pty.lock().unwrap().write_input(&buf[..n]) {
                break;
            }
        }
        let mut data = h2.data.lock().unwrap();
        if data.as_ref().is_some_and(|d| Arc::ptr_eq(d, &stream)) {
            *data = None;
        }
    });

    let child_pid = hosted.pty.lock().unwrap().child_pid;
    let mut m = serde_json::Map::new();
    m.insert("pipe".into(), Value::String(data_name));
    m.insert("cols".into(), json!(cols));
    m.insert("rows".into(), json!(rows));
    m.insert("childPid".into(), json!(child_pid));
    m.insert("hasExited".into(), json!(hosted.exited.load(Ordering::SeqCst)));
    m.insert("exitCode".into(), json!(hosted.exit_code.load(Ordering::SeqCst)));
    m.insert("modes".into(), Value::String(modes));
    m.insert("scrollback".into(), Value::Array(scrollback.into_iter().map(Value::String).collect()));
    ok(m)
}

/// Plain text of one scrollback row (DumpHistoryRow conventions: skip spacers, trim end).
fn dump_history_row(t: &Terminal, index: usize) -> String {
    let cols = t.emu.screen().cols();
    let mut s = String::new();
    for c in 0..cols {
        let cell = t.emu.get_history_cell(index, c);
        if cell.width == 0 {
            continue;
        }
        if let Some(ch) = char::from_u32(cell.rune as u32) {
            s.push(ch);
        }
    }
    s.trim_end().to_string()
}
