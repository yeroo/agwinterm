using System.Reflection;
using Xunit;

namespace Agwinterm.Pty.Tests;

public class PtyResizeTransactionTests
{
    [Fact]
    public async Task RealResizeCannotInterleaveWithRepaintRestore()
    {
        var transaction = new PtyResizeTransaction();
        using var jiggled = new ManualResetEventSlim();
        using var restore = new ManualResetEventSlim();
        using var attempted = new ManualResetEventSlim();
        var size = (100, 24);
        var repaint = Task.Run(() => transaction.Run(() =>
        {
            var original = size; size = (100, 23); jiggled.Set();
            if (!restore.Wait(10000)) throw new TimeoutException("restore barrier");
            size = original;
        }));
        Task? real = null;
        bool heldDuringPause = false;
        try
        {
            Assert.True(jiggled.Wait(10000));
            var gate = typeof(PtyResizeTransaction).GetField("_gate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(transaction)!;
            heldDuringPause = !Monitor.TryEnter(gate);
            if (!heldDuringPause) Monitor.Exit(gate);
            real = Task.Run(() => { attempted.Set(); transaction.Run(() => size = (132, 40)); });
            Assert.True(attempted.Wait(10000));
        }
        finally { restore.Set(); await repaint; if (real is not null) await real; }
        Assert.True(heldDuringPause);
        Assert.Equal((132, 40), size);
    }

    [Theory]
    [InlineData(0u, 24u)] [InlineData(80u, 0u)] [InlineData(10001u, 24u)]
    [InlineData(80u, 10001u)] [InlineData(uint.MaxValue, 24u)]
    public void MalformedDimensionsRefuseBeforeAllocation(uint cols, uint rows)
        => Assert.False(PtyResizeTransaction.Valid(cols, rows));

    [Theory]
    [InlineData(1u, 1u)] [InlineData(10000u, 10000u)]
    public void SupportedBoundsRemainAccepted(uint cols, uint rows)
        => Assert.True(PtyResizeTransaction.Valid(cols, rows));
}
