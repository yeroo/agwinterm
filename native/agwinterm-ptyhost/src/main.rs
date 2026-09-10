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
mod creation;
mod freshenv;
mod persist;
mod pipes;
mod proto;
mod resize;

use std::collections::HashMap;
use std::fs::File;
use std::io::{Read, Write};
use std::sync::atomic::{AtomicBool, AtomicI32, AtomicU64, Ordering};
use std::sync::{Arc, Mutex};
use std::time::Instant;

use agwinterm_core::emulator::Terminal;
use conpty::ConPty;
use pipes::{OvStream, OverlappedPipeServer, PipeServer};
use prost::Message;
use proto::{
    AttachReply, CreateReply, HelloReply, ListReply, Reply, Request, SessionInfo, reply, request,
};

const PROTOCOL_VERSION: u32 = 2;

#[cfg(test)]
mod creation_host_tests {
    use super::*;
    // Real ConPTY test: run only under the integration-suite lease locally.
    #[test]
    fn cancellation_between_spawn_and_publication_cleans_exact_child() {
        let (reached_tx, reached_rx) = std::sync::mpsc::channel();
        let (release_tx, release_rx) = std::sync::mpsc::channel();
        let release_rx = Mutex::new(release_rx);
        let host = Arc::new(Host {
            app_id: "private-creation-barrier".into(),
            state: Mutex::new(HostState {
                sessions: HashMap::new(),
                creations: creation::Tickets::new(),
            }),
            attach_seq: AtomicU64::new(0),
            before_publication: Some(Box::new(move |h| {
                reached_tx.send(h.clone()).unwrap();
                let _ = release_rx
                    .lock()
                    .unwrap()
                    .recv_timeout(std::time::Duration::from_secs(15));
            })),
        });
        let ticket = "0123456789abcdef0123456789abcdef";
        host.state
            .lock()
            .unwrap()
            .creations
            .prepare("barrier", ticket.into(), Instant::now())
            .unwrap();
        let creator = host.clone();
        let join = std::thread::spawn(move || {
            handle_create(
                &creator,
                proto::Create {
                    id: "barrier".into(),
                    creation_ticket: ticket.into(),
                    app: "powershell.exe".into(),
                    args: vec![
                        "-NoLogo".into(),
                        "-NoProfile".into(),
                        "-NonInteractive".into(),
                        "-Command".into(),
                        "Start-Sleep -Seconds 120".into(),
                    ],
                    cols: 80,
                    rows: 24,
                    fresh_env_off: true,
                    ..Default::default()
                },
            )
        });
        let reference = proto::CreationRef {
            id: "barrier".into(),
            ticket: ticket.into(),
        };
        let observed = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| {
            let h = reached_rx
                .recv_timeout(std::time::Duration::from_secs(10))
                .unwrap();
            {
                let mut state = host.state.lock().unwrap();
                assert!(state.sessions.is_empty());
                assert_eq!(
                    state
                        .creations
                        .find("barrier", ticket, Instant::now())
                        .unwrap()
                        .phase,
                    creation::Phase::Creating
                );
            }
            let reply = handle_cancel(&host, &reference);
            assert!(
                matches!(reply.body, Some(reply::Body::Creation(c)) if c.phase == proto::CreationPhase::CreationCancelling as i32)
            );
            h
        }));
        let _ = release_tx.send(());
        let created = join.join();
        let cleaned = handle_cancel(&host, &reference);
        let h = match observed {
            Ok(h) => h,
            Err(e) => std::panic::resume_unwind(e),
        };
        assert!(!created.unwrap().ok);
        assert!(
            matches!(cleaned.body, Some(reply::Body::Creation(c)) if c.phase == proto::CreationPhase::CreationUnknown as i32)
        );
        assert!(host.state.lock().unwrap().sessions.is_empty());
        // h retains the ORIGINAL process handle; never reopen its PID after cancellation.
        let child = h.pty.lock().unwrap().child;
        unsafe {
            assert_eq!(
                windows_sys::Win32::System::Threading::WaitForSingleObject(child, 5000),
                windows_sys::Win32::Foundation::WAIT_OBJECT_0
            );
        }
    }
}

struct Hosted {
    id: String,
    creation_ticket: String,
    pty: Mutex<ConPty>,
    term: Mutex<Terminal>,
    data: Mutex<Option<Arc<OvStream>>>,
    resize: Mutex<()>, // real resize and the complete repaint jiggle share one transaction
    exited: AtomicBool,
    input_closed: AtomicBool, // observed child death/write failure, before output settlement
    /// Bytes the pump has fed so far; the exit watcher's settle window reads it (#246).
    pump_bytes: AtomicU64,
    /// True while a chunk is read but not yet fed + forwarded (the feed can block on `term`).
    pump_in_flight: AtomicBool,
    exit_code: AtomicI32,
}

struct Host {
    app_id: String,
    state: Mutex<HostState>,
    attach_seq: AtomicU64,
    #[cfg(test)]
    before_publication: Option<Box<dyn Fn(&Arc<Hosted>) + Send + Sync>>,
}
struct HostState {
    sessions: HashMap<String, Arc<Hosted>>,
    creations: creation::Tickets<Arc<Hosted>>,
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
        state: Mutex::new(HostState {
            sessions: HashMap::new(),
            creations: creation::Tickets::new(),
        }),
        attach_seq: AtomicU64::new(0),
        #[cfg(test)]
        before_publication: None,
    });

    let cleanup_host = host.clone();
    std::thread::Builder::new()
        .name("creation-cleanup".into())
        .spawn(move || {
            loop {
                std::thread::sleep(std::time::Duration::from_millis(500));
                let pending = cleanup_host
                    .state
                    .lock()
                    .unwrap()
                    .creations
                    .claim_pending_cleanup();
                for (ticket, h) in pending {
                    finish_cleanup(&cleanup_host, &ticket, &h);
                }
            }
        })
        .expect("creation cleanup worker must start before accepting requests");
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
            let cleanup = {
                let mut state = host.state.lock().unwrap();
                let cleanup = state.creations.stop();
                state.sessions.clear();
                cleanup
            };
            for (ticket, s) in cleanup {
                finish_cleanup(&host, &ticket, &s);
            }
            let deadline = Instant::now();
            let mut warned = false;
            while !host.state.lock().unwrap().creations.is_empty() {
                if !warned && deadline.elapsed().as_secs() >= 30 {
                    eprintln!(
                        "shutdown cleanup remains pending; host retained for exact-ticket queries"
                    );
                    warned = true; // diagnostic deadline, not abandonment of eventual shutdown
                }
                std::thread::sleep(std::time::Duration::from_millis(10));
            }
            std::process::exit(0);
        }
    }
}

fn ok_reply(body: Option<reply::Body>) -> Reply {
    Reply {
        ok: true,
        error: String::new(),
        body,
    }
}

fn err_reply(msg: impl Into<String>) -> Reply {
    Reply {
        ok: false,
        error: msg.into(),
        body: None,
    }
}

fn dispatch(host: &Arc<Host>, req: Request) -> Reply {
    match req.cmd {
        Some(request::Cmd::Hello(h)) => {
            if h.protocol == PROTOCOL_VERSION {
                ok_reply(Some(reply::Body::Hello(HelloReply {
                    protocol: PROTOCOL_VERSION,
                    pid: std::process::id(),
                    creation_revision: 1,
                })))
            } else {
                err_reply(format!(
                    "protocol mismatch: host={PROTOCOL_VERSION} client={}",
                    h.protocol
                ))
            }
        }
        Some(request::Cmd::Create(c)) => handle_create(host, c),
        Some(request::Cmd::PrepareCreate(p)) => handle_prepare(host, &p.id),
        Some(request::Cmd::QueryCreate(q)) => handle_query(host, &q),
        Some(request::Cmd::CancelCreate(c)) => handle_cancel(host, &c),
        Some(request::Cmd::Attach(a)) => handle_attach(host, a),
        Some(request::Cmd::Detach(d)) => with_session(host, &d.id, &d.creation_ticket, |h| {
            detach(h);
            ok_reply(None)
        }),
        Some(request::Cmd::Resize(r)) => with_session(host, &r.id, &r.creation_ticket, |h| {
            if !resize::valid(r.cols, r.rows) {
                return err_reply("resize cols/rows must be in 1..10000");
            }
            resize::transaction(&h.resize, || {
                h.term
                    .lock()
                    .unwrap()
                    .emu
                    .resize(r.cols as usize, r.rows as usize);
                h.pty.lock().unwrap().resize(r.cols as i16, r.rows as i16);
            });
            ok_reply(None)
        }),
        Some(request::Cmd::Kill(k)) => {
            let ticket = {
                let state = host.state.lock().unwrap();
                let Some(h) = state.sessions.get(&k.id) else {
                    return err_reply(format!("no session '{}'", k.id));
                };
                if !k.creation_ticket.is_empty() && k.creation_ticket != h.creation_ticket {
                    return err_reply("session incarnation changed");
                }
                h.creation_ticket.clone()
            };
            let result = handle_cancel(host, &proto::CreationRef { id: k.id, ticket });
            match result.body {
                Some(reply::Body::Creation(c))
                    if c.phase == proto::CreationPhase::CreationUnknown as i32 =>
                {
                    ok_reply(None)
                }
                _ => err_reply("session cleanup is pending"),
            }
        }
        Some(request::Cmd::List(_)) => {
            let sessions: Vec<_> = host
                .state
                .lock()
                .unwrap()
                .sessions
                .values()
                .cloned()
                .collect();
            let mut list = ListReply {
                sessions: Vec::new(),
            };
            for h in sessions {
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
                    creation_ticket: h.creation_ticket.clone(),
                });
            }
            ok_reply(Some(reply::Body::List(list)))
        }
        Some(request::Cmd::Shutdown(_)) => ok_reply(None),
        None => err_reply("unknown command"),
    }
}

fn with_session(
    host: &Arc<Host>,
    id: &str,
    ticket: &str,
    act: impl FnOnce(&Arc<Hosted>) -> Reply,
) -> Reply {
    let h = host.state.lock().unwrap().sessions.get(id).cloned();
    match h {
        Some(h) if !ticket.is_empty() && ticket != h.creation_ticket => {
            err_reply("session incarnation changed")
        }
        Some(h) => act(&h),
        None => err_reply(format!("no session '{id}'")),
    }
}

/// One 50 ms window with no new pump bytes and no chunk in flight, at most ten windows. A pump that
/// already stopped costs one; the cap bounds an exit, never a hang. Holds no lock.
fn settle_output(h: &Arc<Hosted>) {
    let mut seen = h.pump_bytes.load(Ordering::SeqCst);
    for _ in 0..10 {
        std::thread::sleep(std::time::Duration::from_millis(50));
        let now = h.pump_bytes.load(Ordering::SeqCst);
        if now == seen && !h.pump_in_flight.load(Ordering::SeqCst) {
            return;
        }
        seen = now;
    }
}

fn detach(h: &Arc<Hosted>) {
    let mut data = h.data.lock().unwrap();
    if let Some(d) = data.take() {
        d.cancel_io(); // wake the input pump; last Arc drop closes the handle = client EOF
    }
}

fn new_creation_ticket() -> Option<String> {
    use windows_sys::Win32::Security::Cryptography::{
        BCRYPT_USE_SYSTEM_PREFERRED_RNG, BCryptGenRandom,
    };
    let mut bytes = [0u8; 16];
    let status = unsafe {
        BCryptGenRandom(
            std::ptr::null_mut(),
            bytes.as_mut_ptr(),
            16,
            BCRYPT_USE_SYSTEM_PREFERRED_RNG,
        )
    };
    (status >= 0).then(|| bytes.iter().map(|b| format!("{b:02x}")).collect())
}
fn creation_reply(id: &str, ticket: &str, phase: Option<creation::Phase>) -> Reply {
    use proto::CreationPhase as P;
    let state = match phase {
        None => P::CreationUnknown,
        Some(creation::Phase::Prepared) => P::CreationPrepared,
        Some(creation::Phase::Creating) => P::CreationCreating,
        Some(creation::Phase::Live) => P::CreationLive,
        Some(creation::Phase::Cancelling) => P::CreationCancelling,
    };
    ok_reply(Some(reply::Body::Creation(proto::CreationReply {
        id: id.into(),
        ticket: ticket.into(),
        phase: state as i32,
    })))
}
fn handle_prepare(host: &Arc<Host>, id: &str) -> Reply {
    let Some(ticket) = new_creation_ticket() else {
        return err_reply("creation ticket entropy unavailable");
    };
    let mut state = host.state.lock().unwrap();
    match state.creations.prepare(id, ticket, Instant::now()) {
        Some(ticket) => creation_reply(id, &ticket, Some(creation::Phase::Prepared)),
        None => err_reply("missing id, host stopping or preparation limit reached"),
    }
}
fn handle_query(host: &Arc<Host>, request: &proto::CreationRef) -> Reply {
    let mut state = host.state.lock().unwrap();
    creation_reply(
        &request.id,
        &request.ticket,
        state
            .creations
            .find(&request.id, &request.ticket, Instant::now())
            .map(|e| e.phase),
    )
}
fn dispose_hosted(h: &Arc<Hosted>) -> bool {
    detach(h);
    h.pty.lock().unwrap().kill_and_close()
}
fn handle_cancel(host: &Arc<Host>, request: &proto::CreationRef) -> Reply {
    let claimed = {
        let mut state = host.state.lock().unwrap();
        if state
            .creations
            .find(&request.id, &request.ticket, Instant::now())
            .is_none()
        {
            return creation_reply(&request.id, &request.ticket, None);
        }
        let claimed = state.creations.cancel(&request.ticket);
        if let Some(h) = &claimed {
            if state
                .sessions
                .get(&h.id)
                .is_some_and(|current| Arc::ptr_eq(current, h))
            {
                state.sessions.remove(&h.id);
            }
        }
        claimed
    };
    if let Some(h) = claimed {
        finish_cleanup(host, &request.ticket, &h);
    }
    handle_query(host, request)
}
fn finish_cleanup(host: &Arc<Host>, ticket: &str, h: &Arc<Hosted>) {
    let cleaned = dispose_hosted(h);
    let mut state = host.state.lock().unwrap();
    if cleaned {
        state.creations.cleaned(ticket);
    } else {
        state.creations.cleanup_failed(ticket, h.clone());
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
    let Some((cols, rows)) = resize::create_dimensions(c.cols, c.rows) else {
        return err_reply("create cols/rows must not exceed 10000");
    };
    let ticket = {
        let mut state = host.state.lock().unwrap();
        let ticket = if c.creation_ticket.is_empty() {
            let Some(ticket) = new_creation_ticket()
                .and_then(|ticket| state.creations.prepare(&c.id, ticket, Instant::now()))
            else {
                return err_reply("creation ticket unavailable");
            };
            ticket
        } else {
            c.creation_ticket.clone()
        };
        if !c.creation_ticket.is_empty()
            && state
                .creations
                .find(&c.id, &ticket, Instant::now())
                .is_some_and(|e| e.phase == creation::Phase::Live)
        {
            return ok_reply(Some(reply::Body::Create(CreateReply {
                id: c.id,
                creation_ticket: ticket,
            })));
        }
        if !state.creations.begin(&c.id, &ticket, Instant::now()) {
            return if state
                .sessions
                .keys()
                .any(|id| id.eq_ignore_ascii_case(&c.id))
            {
                err_reply(format!("session '{}' already exists", c.id))
            } else {
                err_reply("unknown, expired, pending or cancelled creation ticket")
            };
        }
        ticket
    };

    let fresh_env = !c.fresh_env_off;
    let env: Option<Vec<(String, String)>> = if fresh_env || !c.env.is_empty() {
        let mut base = if fresh_env {
            freshenv::fresh_environment()
        } else {
            std::env::vars().collect()
        };
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

    let cwd = if c.cwd.is_empty() {
        None
    } else {
        Some(c.cwd.as_str())
    };
    let mut pty = match ConPty::spawn(
        &c.app,
        &c.args,
        c.verbatim,
        cwd,
        env.as_deref(),
        cols as i16,
        rows as i16,
    ) {
        Ok(p) => p,
        Err(e) => {
            host.state.lock().unwrap().creations.cleaned(&ticket);
            return err_reply(format!("spawn failed: {e}"));
        }
    };
    let mut out = pty
        .output
        .take()
        .expect("new ConPTY owns its output reader");

    let hosted = Arc::new(Hosted {
        id: c.id.clone(),
        creation_ticket: ticket.clone(),
        term: Mutex::new(Terminal::new(cols as usize, rows as usize)),
        pty: Mutex::new(pty),
        data: Mutex::new(None),
        resize: Mutex::new(()),
        exited: AtomicBool::new(false),
        input_closed: AtomicBool::new(false),
        pump_bytes: AtomicU64::new(0),
        pump_in_flight: AtomicBool::new(false),
        exit_code: AtomicI32::new(0),
    });
    let hosted2 = hosted.clone();
    let pump_hosted = hosted.clone();

    // Output pump: ConPTY → emulator (+ forward raw to the attached client).
    if std::thread::Builder::new()
        .spawn(move || {
            let hosted = pump_hosted;
            let mut buf = [0u8; 64 * 1024];
            loop {
                let n = out.read(&mut buf).unwrap_or(0);
                if n == 0 {
                    break;
                }
                hosted.pump_in_flight.store(true, Ordering::SeqCst);
                hosted.term.lock().unwrap().feed(&buf[..n]);
                let mut data = hosted.data.lock().unwrap();
                if let Some(d) = data.as_ref()
                    && !d.write_all(&buf[..n])
                {
                    *data = None; // client vanished mid-write -> plain detach
                }
                drop(data);
                // After the feed and the forward: "settled" means the emulator and the client both have it.
                hosted.pump_bytes.fetch_add(n as u64, Ordering::SeqCst);
                hosted.pump_in_flight.store(false, Ordering::SeqCst);
            }
        })
        .is_err()
    {
        finish_cleanup(host, &ticket, &hosted);
        return err_reply("output drainer thread could not start");
    }

    // Exit watcher on the raw child handle — ConPTY's output pipe does NOT EOF on
    // child exit; waiting via the pty mutex would hold it for the child's lifetime.
    let child_h = hosted2.pty.lock().unwrap().child as usize;
    if std::thread::Builder::new()
        .spawn(move || {
            let code = conpty::wait_child(child_h);
            hosted2.input_closed.store(true, Ordering::SeqCst);
            // The child is gone, but what it wrote last may still be in flight: conhost flushes the
            // pseudoconsole's output after the process exits and the pipe never hits EOF, so "exited"
            // is not "complete". Wait until one 50 ms window passes with no new bytes from the pump,
            // at most 500 ms, BEFORE the client's EOF (the detach) and `has_exited` say the session
            // ended — the same window as TerminalSession's SettleOutput (#246).
            settle_output(&hosted2);
            hosted2.exit_code.store(code, Ordering::SeqCst);
            hosted2.exited.store(true, Ordering::SeqCst);
            detach(&hosted2); // data-pipe EOF = the client's exit signal
        })
        .is_err()
    {
        finish_cleanup(host, &ticket, &hosted);
        return err_reply("exit watcher thread could not start");
    }

    #[cfg(test)]
    if let Some(barrier) = &host.before_publication {
        barrier(&hosted);
    }
    let publish = {
        let mut state = host.state.lock().unwrap();
        let publish = state.creations.complete(&ticket, hosted.clone());
        if publish {
            state.sessions.insert(c.id.clone(), hosted.clone());
        }
        publish
    };
    if !publish {
        finish_cleanup(host, &ticket, &hosted);
        return err_reply("creation cancelled; query exact ticket for cleanup completion");
    }
    ok_reply(Some(reply::Body::Create(CreateReply {
        id: c.id,
        creation_ticket: ticket,
    })))
}

fn handle_attach(host: &Arc<Host>, a: proto::Attach) -> Reply {
    let Some(hosted) = host.state.lock().unwrap().sessions.get(&a.id).cloned() else {
        return err_reply(format!("no session '{}'", a.id));
    };
    if !a.creation_ticket.is_empty() && a.creation_ticket != hosted.creation_ticket {
        return err_reply("session incarnation changed");
    }
    let seq = host.attach_seq.fetch_add(1, Ordering::SeqCst);
    let data_name = format!("{}-ptyhost-d-{seq:08x}", host.app_id);
    let server = match OverlappedPipeServer::create(&data_name) {
        Ok(s) => s,
        Err(e) => return err_reply(e),
    };

    // Snapshot under the emulator lock, before any new output can race the seed.
    let (scrollback, scrollback_blob, modes, cols, rows) = {
        let t = hosted.term.lock().unwrap();
        let mut lines = Vec::with_capacity(t.emu.history_count());
        for h in 0..t.emu.history_count() {
            lines.push(dump_history_row(&t, h));
        }
        let (c, r) = (t.emu.screen().cols(), t.emu.screen().rows());
        (lines, serialize_history(&t.emu), t.emu.dump_modes(), c, r)
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
            resize::transaction(&h2.resize, || {
                let (c, r) = h2.pty.lock().unwrap().size();
                h2.term
                    .lock()
                    .unwrap()
                    .emu
                    .resize(c as usize, (r as usize).saturating_sub(1).max(2));
                h2.pty.lock().unwrap().resize(c, (r - 1).max(2));
                std::thread::sleep(std::time::Duration::from_millis(60));
                h2.term.lock().unwrap().emu.resize(c as usize, r as usize);
                h2.pty.lock().unwrap().resize(c, r);
            });
        }
        let mut buf = [0u8; 16 * 1024];
        loop {
            let n = stream.read(&mut buf);
            if n == 0 {
                break;
            }
            if !h2.input_closed.load(Ordering::SeqCst)
                && !h2.pty.lock().unwrap().write_input(&buf[..n])
            {
                // Keep the output channel until the exit watcher drains it and detaches.
                h2.input_closed.store(true, Ordering::SeqCst);
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
        scrollback_blob, // attributed history (full colour on reattach), byte-identical to the C# host
        creation_ticket: hosted.creation_ticket.clone(),
    })))
}

/// Serialize the emulator's HISTORY as a persist.PBuffer blob, BYTE-IDENTICAL to the C#
/// BufferPersist.Serialize(includeVisible: false) so the C# client restores it unchanged:
/// per-row trailing-EMPTY trim, drop trailing all-empty rows, cap at 500 (newest kept).
fn serialize_history(emu: &agwinterm_core::emulator::Emulator) -> Vec<u8> {
    use agwinterm_core::cell::{Cell, Color};
    use prost::Message;
    let pack = |c: Color| ((c.r as u32) << 16) | ((c.g as u32) << 8) | c.b as u32;
    let cols = emu.screen().cols();
    let mut buf = persist::PBuffer {
        version: 1,
        cols: cols as u32,
        rows: Vec::new(),
    };
    for h in 0..emu.history_count() {
        let mut len = cols;
        while len > 0 && emu.get_history_cell(h, len - 1) == Cell::EMPTY {
            len -= 1;
        }
        let mut row = persist::PRow {
            cells: Vec::with_capacity(len),
        };
        for col in 0..len {
            let c = emu.get_history_cell(h, col);
            row.cells.push(persist::PCell {
                rune: c.rune,
                fg: pack(c.foreground),
                bg: pack(c.background),
                attrs: c.attributes,
                width: c.width as u32,
                fg_kind: c.fg_spec.kind as u32,
                fg_index: c.fg_spec.index as u32,
                fg_rgb: pack(c.fg_spec.rgb),
                bg_kind: c.bg_spec.kind as u32,
                bg_index: c.bg_spec.index as u32,
                bg_rgb: pack(c.bg_spec.rgb),
            });
        }
        buf.rows.push(row);
    }
    while buf.rows.last().is_some_and(|r| r.cells.is_empty()) {
        buf.rows.pop();
    }
    if buf.rows.len() > 500 {
        buf.rows.drain(0..buf.rows.len() - 500);
    }
    buf.encode_to_vec()
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
