using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Porta.Pty;

namespace Agwinterm.Pty;

/// <summary>
/// An <see cref="IPtyConnection"/> over an <b>externally-created</b> pseudoconsole — agwinterm does NOT
/// spawn the child; it's handed the pipe/signal handles that another process (conhost/OpenConsole in the
/// Windows "default terminal" handoff, or a test harness) already set up. Stage A of defterm (T2-13):
/// this is what lets agwinterm host a console session it didn't start.
/// </summary>
internal sealed class AttachedPty : IPtyConnection
{
    private readonly FileStream _reader;   // console VT output → we read
    private readonly FileStream _writer;   // user input → we write
    private readonly SafeFileHandle _signal;
    private IntPtr _clientProcess;         // the console client, for exit/wait (IntPtr.Zero if unknown)

    public Stream ReaderStream => _reader;
    public Stream WriterStream => _writer;
    public int Pid { get; }
    public int ExitCode { get { if (_clientProcess != IntPtr.Zero && GetExitCodeProcess(_clientProcess, out int c)) return c; return 0; } }
#pragma warning disable CS0067 // IPtyConnection member; exit is driven via WaitForExit, not the event
    public event EventHandler<PtyExitedEventArgs>? ProcessExited;
#pragma warning restore CS0067

    /// <param name="conOut">Handle the terminal READS console VT output from.</param>
    /// <param name="conIn">Handle the terminal WRITES user input to.</param>
    /// <param name="signal">The ConPTY signal pipe (resize etc.); may be invalid/closed.</param>
    /// <param name="clientProcess">The console client process handle for exit detection (optional).</param>
    /// <param name="pid">The client PID (0 if unknown).</param>
    public AttachedPty(SafeFileHandle conOut, SafeFileHandle conIn, SafeFileHandle signal, IntPtr clientProcess, int pid)
    {
        _reader = new FileStream(conOut, FileAccess.Read);
        _writer = new FileStream(conIn, FileAccess.Write);
        _signal = signal;
        _clientProcess = clientProcess;
        Pid = pid;
    }

    public void Resize(int cols, int rows)
    {
        // ConPTY signal-pipe resize packet: [PTY_SIGNAL_RESIZE_WINDOW=8][cols][rows] as little-endian UINT16s.
        // NOTE: byte[] (not Span) — classic DllImport can't marshal Span without assembly-wide
        // DisableRuntimeMarshalling; the old Span overload threw MarshalDirectiveException on every
        // call (swallowed below), so attached consoles never saw resizes and painted at a stale width.
        if (_signal is null || _signal.IsInvalid || _signal.IsClosed) return;
        byte[] pkt =
        {
            8, 0,
            (byte)(cols & 0xFF), (byte)((cols >> 8) & 0xFF),
            (byte)(rows & 0xFF), (byte)((rows >> 8) & 0xFF),
        };
        try
        {
            if (!WriteFile(_signal, pkt, (uint)pkt.Length, out _, IntPtr.Zero))
                Log($"resize signal write failed: err={Marshal.GetLastWin32Error()}");
        }
        catch (Exception ex) { Log($"resize signal write threw: {ex.GetType().Name} {ex.Message}"); }
    }

    private static readonly string? LogPath = Environment.GetEnvironmentVariable("AGWINTERM_PTYLOG");
    private static void Log(string m) { if (LogPath is not null) try { File.AppendAllText(LogPath, $"[attached] {m}\n"); } catch { } }

    public bool WaitForExit(int milliseconds)
    {
        if (_clientProcess == IntPtr.Zero) return false;   // no handle → caller falls back to EOF on the reader
        return WaitForSingleObject(_clientProcess, milliseconds < 0 ? 0xFFFFFFFF : (uint)milliseconds) == 0;
    }

    public void Kill()
    {
        if (_clientProcess != IntPtr.Zero) try { TerminateProcess(_clientProcess, 1); } catch { }
    }

    public void Dispose()
    {
        try { _writer.Dispose(); } catch { }
        try { _reader.Dispose(); } catch { }
        try { _signal?.Dispose(); } catch { }
        if (_clientProcess != IntPtr.Zero) { CloseHandle(_clientProcess); _clientProcess = IntPtr.Zero; }
    }

    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool WriteFile(SafeFileHandle h, byte[] buf, uint n, out uint written, IntPtr overlapped);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint WaitForSingleObject(IntPtr h, uint ms);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetExitCodeProcess(IntPtr h, out int code);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool TerminateProcess(IntPtr h, uint code);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CloseHandle(IntPtr h);
}
