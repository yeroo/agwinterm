using System.Runtime.InteropServices;
using System.Text;

namespace Agwinterm.Pty;

/// <summary>The session.new command contract, shared with Lite's session_command.h.</summary>
public sealed record SessionCommand(string App, string[] Args)
{
    // Porta.Pty's automatic quoting changes embedded quotes. Supply Windows-quoted
    // arguments verbatim; the Rust and managed hosts also accept this representation.
    public string[] QuotedArgs => Array.ConvertAll(Args, TerminalSession.QuoteArg);

    public static bool TryCreate(string? command, string? mode, bool wait, string? profile,
        out SessionCommand? launch, out string error)
    {
        launch = null;
        error = "";
        if (mode is not (null or "powershell" or "direct"))
            error = "command-mode must be powershell or direct";
        else if (string.IsNullOrWhiteSpace(command))
        {
            if (mode is not null || wait) error = "command-mode and wait require a nonempty command";
        }
        else if (profile is not null) error = "command and profile are mutually exclusive";
        else if (command.Contains('\0')) error = "command must not contain NUL";
        else if (mode == "direct" && wait) error = "wait requires powershell mode; direct mode does not add a shell";
        else if (mode == "direct")
        {
            // Use Windows argv rules, including escaped quotes, empty arguments and trailing
            // backslashes. Leading whitespace must not manufacture an empty executable.
            nint words = CommandLineToArgvW(command.TrimStart(), out int count);
            if (words == 0) error = "could not parse command";
            else
            {
                try
                {
                    var argv = new string[count];
                    for (int i = 0; i < count; i++)
                        argv[i] = Marshal.PtrToStringUni(Marshal.ReadIntPtr(words, i * IntPtr.Size))!;
                    if (count == 0 || argv[0].Length == 0) error = "command requires an executable";
                    else launch = new(argv[0], argv[1..]);
                }
                finally { LocalFree(words); }
            }
        }
        else launch = new("powershell.exe", ["-NoLogo", "-NoExit", "-Command", command]);

        // Lite's native host fields are bounded. Apply the same limits before either host
        // creates a workspace or session, rather than silently changing a long command.
        if (launch is not null && (Encoding.UTF8.GetByteCount(launch.App) >= 260 ||
            launch.Args.Length > 16 || launch.Args.Any(a => Encoding.UTF8.GetByteCount(a) >= 2048)))
            error = "command exceeds host capacity (app 259 bytes, 16 arguments, 2047 bytes each)";
        if (error.Length == 0) return true;
        launch = null;
        error = "session.new: " + error + "; nothing created";
        return false;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CommandLineToArgvW(string command, out int count);
    [DllImport("kernel32.dll")]
    private static extern nint LocalFree(nint memory);
}
