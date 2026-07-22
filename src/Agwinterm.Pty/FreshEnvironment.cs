using System.Runtime.InteropServices;

namespace Agwinterm.Pty;

/// <summary>
/// Builds the environment a GENUINELY FRESH process tree would receive right now: Windows
/// (userenv) re-reads machine + user variables from the registry, composes PATH as
/// machine-then-user, and expands REG_EXPAND_SZ values — instead of the snapshot this process
/// inherited when IT started. This is how a new tab sees a JDK installed five minutes ago
/// without restarting agwinterm (or the pty-host, which can be even longer-lived). Same
/// semantics as Windows Terminal's <c>compatibility.reloadEnvironmentVariables</c> (also
/// default-on there); the <c>fresh-env</c> config key is the escape hatch.
/// </summary>
public static class FreshEnvironment
{
    /// <summary>The freshly generated environment for the current user, or null if Windows
    /// refused — callers fall back to the inherited process env (the pre-feature behavior).</summary>
    public static Dictionary<string, string>? TryBuild()
    {
        IntPtr block = IntPtr.Zero;
        try
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            if (!CreateEnvironmentBlock(out block, identity.Token, inherit: false) || block == IntPtr.Zero)
                return null;

            // The block is a run of NUL-terminated "NAME=value" UTF-16 strings, double-NUL at the end.
            var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            IntPtr p = block;
            while (Marshal.PtrToStringUni(p) is { Length: > 0 } entry)
            {
                p += (entry.Length + 1) * 2;
                int eq = entry.IndexOf('=');
                if (eq <= 0) continue;   // skip malformed entries and "=C:=..." drive-cwd pseudo-vars
                env[entry[..eq]] = entry[(eq + 1)..];
            }
            return env.Count > 0 ? env : null;
        }
        catch { return null; }
        finally { if (block != IntPtr.Zero) DestroyEnvironmentBlock(block); }
    }

    [DllImport("userenv.dll", SetLastError = true)]
    private static extern bool CreateEnvironmentBlock(out IntPtr lpEnvironment, IntPtr hToken, bool inherit);

    [DllImport("userenv.dll", SetLastError = true)]
    private static extern bool DestroyEnvironmentBlock(IntPtr lpEnvironment);
}
