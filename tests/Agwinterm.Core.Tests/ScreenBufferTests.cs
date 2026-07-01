using Agwinterm.Core;

namespace Agwinterm.Core.Tests;

public class ScreenBufferTests
{
    [Fact]
    public void NewBuffer_IsAllEmpty()
    {
        var b = new ScreenBuffer(3, 2);
        Assert.Equal(3, b.Cols);
        Assert.Equal(2, b.Rows);
        for (int r = 0; r < 2; r++)
            for (int c = 0; c < 3; c++)
                Assert.Equal(Cell.Empty, b[r, c]);
    }

    [Fact]
    public void Indexer_SetAndGet()
    {
        var b = new ScreenBuffer(3, 2);
        var cell = Cell.Empty with { Rune = 'X' };
        b[1, 2] = cell;
        Assert.Equal(cell, b[1, 2]);
    }

    [Fact]
    public void Indexer_OutOfRange_Throws()
    {
        var b = new ScreenBuffer(3, 2);
        Assert.Throws<ArgumentOutOfRangeException>(() => b[2, 0]);
        Assert.Throws<ArgumentOutOfRangeException>(() => b[0, 3]);
    }

    [Fact]
    public void Resize_PreservesTopLeft()
    {
        var b = new ScreenBuffer(3, 2);
        b[0, 0] = Cell.Empty with { Rune = 'A' };
        b[1, 2] = Cell.Empty with { Rune = 'B' };
        b.Resize(2, 3);
        Assert.Equal('A', b[0, 0].Rune);          // preserved
        Assert.Equal(Cell.Empty, b[2, 0]);         // new row is empty
        Assert.Equal(2, b.Cols);
        Assert.Equal(3, b.Rows);
    }
}
