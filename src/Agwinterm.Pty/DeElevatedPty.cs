using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Porta.Pty;

namespace Agwinterm.Pty;

/// <summary>
/// A ConPTY connection whose child shell runs at the interactive user's <b>Medium</b> integrity,
/// spawned from an <b>elevated</b> agwinterm (de-elevation). This is the one direction Windows allows
/// — dropping privileges, never raising them — so an elevated window can host both admin and normal
/// sessions. It grabs explorer.exe's token and launches via <c>CreateProcessWithTokenW</c>
/// (needs SeImpersonatePrivilege, which admins hold); Porta.Pty can't do this because it only calls
/// plain <c>CreateProcess</c>.
/// </summary>
internal sealed class DeElevatedPty : IPtyConnection
{
    private IntPtr _hPC;
    private IntPtr _hProcess;
    private IntPtr _hThread;
    private IntPtr _attrList;
    private readonly FileStream _reader;
    private readonly FileStream _writer;

    public Stream ReaderStream => _reader;
    public Stream WriterStream => _writer;
    public int Pid { get; }
    public int ExitCode { get { GetExitCodeProcess(_hProcess, out int c); return c; } }
#pragma warning disable CS0067 // required by IPtyConnection; TerminalSession uses WaitForExit instead
    public event EventHandler<PtyExitedEventArgs>? ProcessExited;
#pragma warning restore CS0067

    private DeElevatedPty(IntPtr hPC, IntPtr hProcess, IntPtr hThread, IntPtr attrList,
        FileStream reader, FileStream writer, int pid)
    {
        _hPC = hPC; _hProcess = hProcess; _hThread = hThread; _attrList = attrList;
        _reader = reader; _writer = writer; Pid = pid;
    }

    public bool WaitForExit(int milliseconds)
        // TerminalSession drives exit via this call, not the event; ProcessExited is left unraised.
        => WaitForSingleObject(_hProcess, milliseconds < 0 ? 0xFFFFFFFF : (uint)milliseconds) == 0;

    public void Resize(int cols, int rows)
    {
        if (_hPC != IntPtr.Zero) ResizePseudoConsole(_hPC, new COORD { X = (short)cols, Y = (short)rows });
    }

    public void Kill()
    {
        if (_hProcess != IntPtr.Zero) TerminateProcess(_hProcess, 1);
    }

    public void Dispose()
    {
        try { _writer.Dispose(); } catch { }
        try { _reader.Dispose(); } catch { }
        if (_hPC != IntPtr.Zero) { ClosePseudoConsole(_hPC); _hPC = IntPtr.Zero; }
        if (_attrList != IntPtr.Zero) { DeleteProcThreadAttributeList(_attrList); Marshal.FreeHGlobal(_attrList); _attrList = IntPtr.Zero; }
        if (_hThread != IntPtr.Zero) { CloseHandle(_hThread); _hThread = IntPtr.Zero; }
        if (_hProcess != IntPtr.Zero) { CloseHandle(_hProcess); _hProcess = IntPtr.Zero; }
    }

    /// <summary>Spawn <paramref name="commandLine"/> de-elevated inside a fresh pseudoconsole. Throws on
    /// failure (e.g. the caller isn't actually elevated, or explorer's token is unavailable).</summary>
    public static DeElevatedPty Spawn(string commandLine, string? cwd, int cols, int rows)
    {
        IntPtr inPipeRead = IntPtr.Zero, inPipeWrite = IntPtr.Zero;
        IntPtr outPipeRead = IntPtr.Zero, outPipeWrite = IntPtr.Zero;
        IntPtr hPC = IntPtr.Zero, attrList = IntPtr.Zero, token = IntPtr.Zero;
        try
        {
            if (!CreatePipe(out inPipeRead, out inPipeWrite, IntPtr.Zero, 0)) throw Fail("CreatePipe(in)");
            if (!CreatePipe(out outPipeRead, out outPipeWrite, IntPtr.Zero, 0)) throw Fail("CreatePipe(out)");

            int hr = CreatePseudoConsole(new COORD { X = (short)cols, Y = (short)rows }, inPipeRead, outPipeWrite, 0, out hPC);
            if (hr != 0) throw new InvalidOperationException($"CreatePseudoConsole failed (0x{hr:x8})");
            // ConPTY dup'd the read/write ends it needs; close our copies so EOF propagates correctly.
            CloseHandle(inPipeRead); inPipeRead = IntPtr.Zero;
            CloseHandle(outPipeWrite); outPipeWrite = IntPtr.Zero;

            // STARTUPINFOEX carrying the pseudoconsole attribute.
            var siEx = new STARTUPINFOEX();
            siEx.StartupInfo.cb = Marshal.SizeOf<STARTUPINFOEX>();
            IntPtr size = IntPtr.Zero;
            InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
            attrList = Marshal.AllocHGlobal(size);
            if (!InitializeProcThreadAttributeList(attrList, 1, 0, ref size)) throw Fail("InitializeProcThreadAttributeList");
            if (!UpdateProcThreadAttribute(attrList, 0, PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE, hPC, (IntPtr)IntPtr.Size, IntPtr.Zero, IntPtr.Zero))
                throw Fail("UpdateProcThreadAttribute");
            siEx.lpAttributeList = attrList;

            var pi = new PROCESS_INFORMATION();
            var cmd = new string(commandLine.ToCharArray());   // mutable buffer for CreateProcess*
            token = GetShellUserToken();   // explorer.exe's Medium primary token
            if (!CreateProcessWithTokenW(token, 0, null, cmd, EXTENDED_STARTUPINFO_PRESENT,
                IntPtr.Zero, string.IsNullOrEmpty(cwd) ? null : cwd, ref siEx, out pi)) throw Fail("CreateProcessWithTokenW");

            var writer = new FileStream(new SafeFileHandle(inPipeWrite, ownsHandle: true), FileAccess.Write);
            var reader = new FileStream(new SafeFileHandle(outPipeRead, ownsHandle: true), FileAccess.Read);
            inPipeWrite = IntPtr.Zero; outPipeRead = IntPtr.Zero;   // owned by the streams now

            return new DeElevatedPty(hPC, pi.hProcess, pi.hThread, attrList, reader, writer, pi.dwProcessId);
        }
        catch
        {
            if (inPipeRead != IntPtr.Zero) CloseHandle(inPipeRead);
            if (inPipeWrite != IntPtr.Zero) CloseHandle(inPipeWrite);
            if (outPipeRead != IntPtr.Zero) CloseHandle(outPipeRead);
            if (outPipeWrite != IntPtr.Zero) CloseHandle(outPipeWrite);
            if (hPC != IntPtr.Zero) ClosePseudoConsole(hPC);
            if (attrList != IntPtr.Zero) { DeleteProcThreadAttributeList(attrList); Marshal.FreeHGlobal(attrList); }
            throw;
        }
        finally { if (token != IntPtr.Zero) CloseHandle(token); }
    }

    /// <summary>Duplicate the interactive shell (explorer.exe) primary token — the logged-in user's
    /// normal Medium-integrity token — so the child launches de-elevated.</summary>
    private static IntPtr GetShellUserToken()
    {
        IntPtr shell = GetShellWindow();
        if (shell == IntPtr.Zero) throw new InvalidOperationException("no shell window (explorer.exe) to de-elevate against");
        GetWindowThreadProcessId(shell, out uint pid);
        IntPtr hProc = OpenProcess(PROCESS_QUERY_INFORMATION, false, pid);
        if (hProc == IntPtr.Zero) throw Fail("OpenProcess(explorer)");
        try
        {
            if (!OpenProcessToken(hProc, TOKEN_DUPLICATE | TOKEN_QUERY, out IntPtr hTok)) throw Fail("OpenProcessToken(explorer)");
            try
            {
                if (!DuplicateTokenEx(hTok, MAXIMUM_ALLOWED, IntPtr.Zero, SecurityImpersonation, TokenPrimary, out IntPtr hPrimary))
                    throw Fail("DuplicateTokenEx");
                return hPrimary;
            }
            finally { CloseHandle(hTok); }
        }
        finally { CloseHandle(hProc); }
    }

    private static InvalidOperationException Fail(string what) =>
        new($"de-elevation: {what} failed (Win32 error {Marshal.GetLastWin32Error()})");

    // ---- P/Invoke ----
    private const int PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE = 0x00020016;
    private const int EXTENDED_STARTUPINFO_PRESENT = 0x00080000;
    private const uint PROCESS_QUERY_INFORMATION = 0x0400;
    private const uint TOKEN_DUPLICATE = 0x0002, TOKEN_QUERY = 0x0008, MAXIMUM_ALLOWED = 0x02000000;
    private const int SecurityImpersonation = 2, TokenPrimary = 1;

    [StructLayout(LayoutKind.Sequential)] private struct COORD { public short X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct PROCESS_INFORMATION { public IntPtr hProcess, hThread; public int dwProcessId, dwThreadId; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public int cb; public string? lpReserved, lpDesktop, lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2; public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }
    [StructLayout(LayoutKind.Sequential)] private struct STARTUPINFOEX { public STARTUPINFO StartupInfo; public IntPtr lpAttributeList; }

    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CreatePipe(out IntPtr hReadPipe, out IntPtr hWritePipe, IntPtr lpPipeAttributes, int nSize);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern int CreatePseudoConsole(COORD size, IntPtr hInput, IntPtr hOutput, uint dwFlags, out IntPtr phPC);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern int ResizePseudoConsole(IntPtr hPC, COORD size);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern void ClosePseudoConsole(IntPtr hPC);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint WaitForSingleObject(IntPtr h, uint ms);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetExitCodeProcess(IntPtr h, out int code);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool TerminateProcess(IntPtr h, uint code);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, int flags, ref IntPtr size);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, IntPtr attribute, IntPtr value, IntPtr size, IntPtr prev, IntPtr ret);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern void DeleteProcThreadAttributeList(IntPtr list);
    [DllImport("user32.dll")] private static extern IntPtr GetShellWindow();
    [DllImport("user32.dll", SetLastError = true)] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool DuplicateTokenEx(IntPtr existing, uint access, IntPtr attrs, int impLevel, int tokenType, out IntPtr newToken);
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessWithTokenW(IntPtr token, int logonFlags, string? appName, string commandLine,
        int creationFlags, IntPtr environment, string? currentDir, ref STARTUPINFOEX startupInfo, out PROCESS_INFORMATION processInfo);
}
