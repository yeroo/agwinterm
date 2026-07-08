using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using Agwinterm.Pty;

namespace Agwinterm.Pty.Tests;

/// <summary>Stage A of defterm (T2-13): agwinterm attaching to an externally-created PTY. Uses raw pipes
/// (no real ConPTY/child needed) to prove Attach pumps external output into the emulator and routes
/// input back out.</summary>
public class PtyAttachTests
{
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CreatePipe(out IntPtr r, out IntPtr w, IntPtr sa, int size);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool WriteFile(SafeFileHandle h, byte[] b, uint n, out uint written, IntPtr o);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool ReadFile(SafeFileHandle h, byte[] b, uint n, out uint read, IntPtr o);

    [Fact]
    public async Task Attach_PumpsExternalOutputIntoEmulator()
    {
        Assert.True(CreatePipe(out IntPtr outR, out IntPtr outW, IntPtr.Zero, 0)); // console output → session reads outR
        Assert.True(CreatePipe(out IntPtr inR, out IntPtr inW, IntPtr.Zero, 0));   // session writes inW → user input

        var session = new TerminalSession(40, 10);
        session.Attach(new SafeFileHandle(outR, true), new SafeFileHandle(inW, true),
                       new SafeFileHandle(IntPtr.Zero, false), IntPtr.Zero, 0);

        var outWrite = new SafeFileHandle(outW, true);
        var bytes = Encoding.ASCII.GetBytes("ATTACH_WORKS\r\n");
        Assert.True(WriteFile(outWrite, bytes, (uint)bytes.Length, out _, IntPtr.Zero));

        for (int i = 0; i < 100 && !RowsContain(session, "ATTACH_WORKS"); i++) await Task.Delay(20);
        Assert.True(RowsContain(session, "ATTACH_WORKS"), "attached output should reach the emulator");

        // Input written to the session must come out the conIn pipe (what a real console would read as stdin).
        var inRead = new SafeFileHandle(inR, true);
        session.Write(Encoding.ASCII.GetBytes("xy"));
        var buf = new byte[8];
        Assert.True(ReadFile(inRead, buf, 2, out uint got, IntPtr.Zero));
        Assert.Equal(2u, got);
        Assert.Equal("xy", Encoding.ASCII.GetString(buf, 0, (int)got));

        outWrite.Dispose(); inRead.Dispose(); session.Dispose();
    }

    private static bool RowsContain(TerminalSession s, string text)
    {
        lock (s.SyncRoot)
            for (int r = 0; r < s.Emulator.Screen.Rows; r++)
                if (s.Emulator.DumpRow(r).Contains(text)) return true;
        return false;
    }
}
