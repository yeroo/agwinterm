using System.Globalization;
using System.IO.Pipes;
using System.Text;

namespace Agwinterm.Ctl;

/// <summary>
/// The CLI's one request/reply exchange over the control pipe, bounded end to end (#315).
///
/// <para>The connect always had a 3 s timeout, but the reply was read with no deadline: an app that
/// accepted the connection and never answered (seen while it was restarting) left the CLI blocked
/// forever. A hook runner that gives up on its own timeout kills the hook, not necessarily this child,
/// so the process leaked. The reply now has a deadline too: <see cref="DefaultTimeout"/> unless the
/// caller passes <c>--timeout</c>, and none for a verb whose reply waits by design
/// (<c>session overlay open --block</c> answers when the overlay closes).</para>
/// </summary>
public static class CtlRequest
{
    /// <summary>Twice the longest bound the app puts on its own work (a UI-thread hop or a CIM process
    /// query: 15 s), so a slow but live reply is never cut off.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    public const int ConnectTimeoutMs = 3000;

    /// <summary>The reply deadline for one call. <paramref name="option"/> is the raw <c>--timeout</c>
    /// value (whole seconds, 0 = no limit) or null when absent: then a verb that
    /// <paramref name="waitsByDesign"/> gets no limit and any other gets <see cref="DefaultTimeout"/>.
    /// Anything but whole seconds is refused rather than read as "no limit".</summary>
    public static bool TryResolveTimeout(string? option, bool waitsByDesign, out TimeSpan timeout, out string? error)
    {
        error = null;
        if (option is null)
        {
            timeout = waitsByDesign ? Timeout.InfiniteTimeSpan : DefaultTimeout;
            return true;
        }
        if (int.TryParse(option, NumberStyles.None, CultureInfo.InvariantCulture, out int seconds))
        {
            timeout = seconds == 0 ? Timeout.InfiniteTimeSpan : TimeSpan.FromSeconds(seconds);
            return true;
        }
        timeout = default;
        error = $"--timeout needs a whole number of seconds (0 waits without a limit), not '{option}'. Nothing sent.";
        return false;
    }

    /// <summary>Send <paramref name="requestJson"/> as one line and read one reply line (null when the app
    /// closed the pipe without answering). A failed connect throws <see cref="TimeoutException"/>, as it
    /// always did; no reply within <paramref name="timeout"/> throws <see cref="ReplyTimeoutException"/>.</summary>
    public static string? Exchange(string pipeName, string requestJson, TimeSpan timeout)
    {
        using var deadline = new CancellationTokenSource(timeout);
        using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        pipe.Connect(ConnectTimeoutMs);
        // The request goes out as raw bytes, not through a StreamWriter: disposing one flushes, and a
        // flush on a pipe the app already hung up throws "Pipe is broken" over the null reply.
        byte[] line = new UTF8Encoding(false).GetBytes(requestJson + Environment.NewLine);
        // leaveOpen so disposing the reader does not close the pipe a second time.
        using var reader = new StreamReader(pipe, Encoding.UTF8, false, 1024, leaveOpen: true);
        try
        {
            // Bound the write without cancelling NamedPipe's write path (#118), as PickCli does;
            // disposal closes the handle if the deadline interrupts the wait.
            pipe.WriteAsync(line, CancellationToken.None).AsTask().WaitAsync(deadline.Token).GetAwaiter().GetResult();
            return reader.ReadLineAsync(deadline.Token).AsTask().GetAwaiter().GetResult();
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            throw new ReplyTimeoutException(timeout);
        }
    }
}

/// <summary>The app accepted the request but sent no reply within the deadline. The request may
/// still have been carried out, so the message says so instead of claiming it failed.</summary>
public sealed class ReplyTimeoutException(TimeSpan timeout)
    : Exception($"no reply from agwinterm within {timeout.TotalSeconds:0} s; the request may still have been carried out (--timeout 0 waits without a limit)")
{
    public TimeSpan Timeout { get; } = timeout;
}
