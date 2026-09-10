namespace Agwinterm.Pty;

/// <summary>A real resize cannot run between the two steps of a reconnect repaint.</summary>
internal sealed class PtyResizeTransaction
{
    private readonly object _gate = new();
    internal void Run(Action action) { lock (_gate) action(); }
    internal static bool Valid(uint cols, uint rows) => cols is >= 1 and <= 10000 && rows is >= 1 and <= 10000;
}
