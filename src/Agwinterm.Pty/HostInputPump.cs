namespace Agwinterm.Pty;

internal static class HostInputPump
{
    // The output half of this duplex channel must survive a child-input failure until the exit
    // watcher settles the output and cancels the channel. Closing here would truncate final output.
    internal static async Task CopyAsync(Stream channel, Action<byte[], int> write, CancellationToken cancellation)
    {
        var buffer = new byte[16 * 1024];
        bool inputClosed = false;
        while (true)
        {
            int n = await channel.ReadAsync(buffer, cancellation).ConfigureAwait(false);
            if (n <= 0) return;
            if (inputClosed) continue;
            try { write(buffer, n); }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
            { inputClosed = true; }
        }
    }
}
