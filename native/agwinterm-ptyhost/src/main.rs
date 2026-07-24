//! agwinterm-ptyhost: the pty-host in Rust, protocol v2 (protobuf wire — the
//! schema in proto/ptyhost.proto is the single source of truth for all four
//! speakers; JSON removed by decision on #134).
//!
//! Control pipe: 4-byte LE length prefix + encoded Request/Reply, strict
//! request/response. DATA pipes stay raw bytes. Blocking threads everywhere —
//! no async, no IOCP.
//!
//! v1-behavior parity notes: deElevate refused loudly; hello is the hard gate.

mod conpty;
mod freshenv;
mod pipes;
mod proto;

use std::collections::HashMap;
use std::fs::File;
use std::io::{Read, Write};
use std::sync::atomic::{AtomicBool, AtomicI32, AtomicU64, Ordering};
use std::sync::{Arc, Mutex};

use agwinterm_core::emulator::Terminal;
use conpty::ConPty;
use pipes::{OverlappedPipeServer, OvStream, PipeServer};
use prost::Message;
use proto::{reply, request, AttachReply, CreateReply, HelloReply, ListReply, Reply, Request, SessionInfo};

const PROTOCOL_VERSION: u32 = 2;

struct Hosted {
    id: String,
    pty: Mutex<ConPty>,
    term: Mutex<Terminal>,
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

fn read_frame(r: &mut impl Read) -> Option<Vec<u8>> {
    let mut len = [0u8; 4];
    r.read_exact(&mut len).ok()?;
    let n = u32::from_le_bytes(len) as usize;
    if n > 16 * 1024 * 1024 {
        return None; // frame cap: no runaway allocations from a garbled client
    }
    let mut buf = vec![0u8; n];
    r.read_exact(&mut buf).ok()?;
    Some(buf)
}

fn write_frame(w: &mut impl Write, bytes: &[u8]) -> bool {
    w.write_all(&(bytes.len() as u32).to_le_bytes()).is_ok()
        && w.write_all(bytes).is_ok()
        && w.flush().is_ok()
}

fn handle_control_client(host: Arc<Host>, stream: File) {
    let mut writer = match stream.try_clone() {
        Ok(w) => w,
        Err(_) => return,
    };
    let mut reader = stream;
    while let Some(frame) = read_frame(&mut reader) {
        let request = match Request::decode(frame.as_slice()) {
            Ok(r) => r,
            Err(_) => Request { cmd: None }, // undecodable → "unknown command" error reply
        };
        let shutdown = matches!(request.cmd, Some(request::Cmd::Shutdown(_)));
        let reply = dispatch(&host, request);
        if !write_frame(&mut writer, &reply.encode_to_vec()) {
            return;
        }
        if shutdown && reply.ok {
            // Ack flushed; tear down after a beat (same grace as v1).
            std::thread::sleep(std::time::Duration::from_millis(100));
            let sessions: Vec<Arc<Hosted>> = host.sessions.lock().unwrap().values().cloned().collect();
            for s in sessions {
                s.pty.lock().unwrap().kill();
            }
            std::process::exit(0);
        }
    }
}

fn ok_reply(body: Option<reply::Body>) -> Reply {
    Reply { ok: true, error: String::new(), body }
}

fn err_reply(msg: impl Into<String>) -> Reply {
    Reply { ok: false, error: msg.into(), body: None }
}

fn dispatch(host: &Arc<Host>, req: Request) -> Reply {
    match req.cmd {
        Some(request::Cmd::Hello(h)) => {
            if h.protocol == PROTOCOL_VERSION {
                ok_reply(Some(reply::Body::Hello(HelloReply {
                    protocol: PROTOCOL_VERSION,
                    pid: std::process::id(),
                })))
            } else {
                err_reply(format!("protocol mismatch: host={PROTOCOL_VERSION} client={}", h.protocol))
            }
        }
        Some(request::Cmd::Create(c)) => handle_create(host, c),
        Some(request::Cmd::Attach(a)) => handle_attach(host, a),
        Some(request::Cmd::Detach(d)) => with_session(host, &d.id, |h| {
            detach(h);
            ok_reply(None)
        }),
        Some(request::Cmd::Resize(r)) => with_session(host, &r.id, |h| {
            if r.cols == 0 || r.rows == 0 {
                return err_reply("resize needs cols/rows");
            }
            h.term.lock().unwrap().emu.resize(r.cols as usize, r.rows as usize);
            h.pty.lock().unwrap().resize(r.cols as i16, r.rows as i16);
            ok_reply(None)
        }),
        Some(request::Cmd::Kill(k)) => {
            match host.sessions.lock().unwrap().remove(&k.id) {
                Some(h) => {
                    detach(&h);
                    h.pty.lock().unwrap().kill();
                    ok_reply(None)
                }
                None => err_reply(format!("no session '{}'", k.id)),
            }
        }
        Some(request::Cmd::List(_)) => {
            let sessions = host.sessions.lock().unwrap();
            let mut list = ListReply { sessions: Vec::new() };
            for h in sessions.values() {
                let (cols, rows) = h.pty.lock().unwrap().size();
                list.sessions.push(SessionInfo {
                    id: h.id.clone(),
                    cols: cols as u32,
                    rows: rows as u32,
                    child_pid: h.pty.lock().unwrap().child_pid,
                    has_exited: h.exited.load(Ordering::SeqCst),
                    exit_code: h.exit_code.load(Ordering::SeqCst),
                    title: h.term.lock().unwrap().emu.title.clone(),
                    attached: h.data.lock().unwrap().is_some(),
                });
            }
            ok_reply(Some(reply::Body::List(list)))
        }
        Some(request::Cmd::Shutdown(_)) => ok_reply(None),
        None => err_reply("unknown command"),
    }
}

fn with_session(host: &Arc<Host>, id: &str, act: impl FnOnce(&Arc<Hosted>) -> Reply) -> Reply {
    let h = host.sessions.lock().unwrap().get(id).cloned();
    match h {
        Some(h) => act(&h),
        None => err_reply(format!("no session '{id}'")),
    }
}

fn detach(h: &Arc<Hosted>) {
    let mut data = h.data.lock().unwrap();
    if let Some(d) = data.take() {
        d.cancel_io(); // wake the input pump; last Arc drop closes the handle = client EOF
    }
}

fn handle_create(host: &Arc<Host>, c: proto::Create) -> Reply {
    if c.id.is_empty() {
        return err_reply("create needs id");
    }
    if c.app.is_empty() {
        return err_reply("create needs app");
    }
    if c.de_elevate {
        return err_reply("spawn failed: de-elevate unsupported by rust host");
    }
    let cols = c.cols.clamp(1, 10000) as i64;
    let rows = c.rows.clamp(1, 10000) as i64;
    {
        let sessions = host.sessions.lock().unwrap();
        if sessions.contains_key(&c.id) {
            return err_reply(format!("session '{}' already exists", c.id));
        }
    }

    let fresh_env = !c.fresh_env_off;
    let env: Option<Vec<(String, String)>> = if fresh_env || !c.env.is_empty() {
        let mut base = if fresh_env { freshenv::fresh_environment() } else { std::env::vars().collect() };
        for (k, v) in &c.env {
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

    let cwd = if c.cwd.is_empty() { None } else { Some(c.cwd.as_str()) };
    let pty = match ConPty::spawn(&c.app, &c.args, c.verbatim, cwd, env.as_deref(), cols as i16, rows as i16) {
        Ok(p) => p,
        Err(e) => return err_reply(format!("spawn failed: {e}")),
    };

    let hosted = Arc::new(Hosted {
        id: c.id.clone(),
        term: Mutex::new(Terminal::new(cols as usize, rows as usize)),
        pty: Mutex::new(pty),
        data: Mutex::new(None),
        exited: AtomicBool::new(false),
        exit_code: AtomicI32::new(0),
    });
    host.sessions.lock().unwrap().insert(c.id.clone(), hosted.clone());
    let hosted2 = hosted.clone();

    // Output pump: ConPTY → emulator (+ forward raw to the attached client).
    std::thread::spawn(move || {
        let mut out = { hosted.pty.lock().unwrap().output.try_clone() };
        let Ok(ref mut out) = out else { return };
        let mut buf = [0u8; 64 * 1024];
        loop {
            let n = out.read(&mut buf).unwrap_or(0);
            if n == 0 {
                break;
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

    // Exit watcher on the raw child handle — ConPTY's output pipe does NOT EOF on
    // child exit; waiting via the pty mutex would hold it for the child's lifetime.
    let child_h = hosted2.pty.lock().unwrap().child as usize;
    std::thread::spawn(move || {
        let code = conpty::wait_child(child_h);
        hosted2.exit_code.store(code, Ordering::SeqCst);
        hosted2.exited.store(true, Ordering::SeqCst);
        detach(&hosted2); // data-pipe EOF = the client's exit signal
    });

    ok_reply(Some(reply::Body::Create(CreateReply { id: c.id })))
}

fn handle_attach(host: &Arc<Host>, a: proto::Attach) -> Reply {
    let Some(hosted) = host.sessions.lock().unwrap().get(&a.id).cloned() else {
        return err_reply(format!("no session '{}'", a.id));
    };
    let seq = host.attach_seq.fetch_add(1, Ordering::SeqCst);
    let data_name = format!("{}-ptyhost-d-{seq:08x}", host.app_id);
    let server = match OverlappedPipeServer::create(&data_name) {
        Ok(s) => s,
        Err(e) => return err_reply(e),
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
    let repaint = a.repaint;
    std::thread::spawn(move || {
        let Ok(stream) = server.accept() else { return };
        let stream = Arc::new(stream);
        {
            let mut data = h2.data.lock().unwrap();
            if let Some(old) = data.take() {
                old.cancel_io(); // supersede: old client EOFs
            }
            *data = Some(stream.clone());
        }
        if h2.exited.load(Ordering::SeqCst) {
            *h2.data.lock().unwrap() = None; // exited while attaching → immediate EOF
            return;
        }
        if repaint {
            let (c, r) = h2.pty.lock().unwrap().size();
            h2.term.lock().unwrap().emu.resize(c as usize, (r as usize).saturating_sub(1).max(2));
            h2.pty.lock().unwrap().resize(c, (r - 1).max(2));
            std::thread::sleep(std::time::Duration::from_millis(60));
            h2.term.lock().unwrap().emu.resize(c as usize, r as usize);
            h2.pty.lock().unwrap().resize(c, r);
        }
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

    ok_reply(Some(reply::Body::Attach(AttachReply {
        pipe: data_name,
        cols: cols as u32,
        rows: rows as u32,
        child_pid: hosted.pty.lock().unwrap().child_pid,
        has_exited: hosted.exited.load(Ordering::SeqCst),
        exit_code: hosted.exit_code.load(Ordering::SeqCst),
        modes,
        scrollback,
    })))
}

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
