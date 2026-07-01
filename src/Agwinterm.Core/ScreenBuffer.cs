namespace Agwinterm.Core;

public sealed class ScreenBuffer
{
    private Cell[] _cells;

    public int Cols { get; private set; }
    public int Rows { get; private set; }

    public ScreenBuffer(int cols, int rows)
    {
        if (cols <= 0) throw new ArgumentOutOfRangeException(nameof(cols));
        if (rows <= 0) throw new ArgumentOutOfRangeException(nameof(rows));
        Cols = cols;
        Rows = rows;
        _cells = new Cell[cols * rows];
        Clear();
    }

    public Cell this[int row, int col]
    {
        get
        {
            CheckBounds(row, col);
            return _cells[row * Cols + col];
        }
        set
        {
            CheckBounds(row, col);
            _cells[row * Cols + col] = value;
        }
    }

    public void Clear() => Array.Fill(_cells, Cell.Empty);

    public void Resize(int cols, int rows)
    {
        if (cols <= 0) throw new ArgumentOutOfRangeException(nameof(cols));
        if (rows <= 0) throw new ArgumentOutOfRangeException(nameof(rows));
        var next = new Cell[cols * rows];
        Array.Fill(next, Cell.Empty);
        int copyCols = Math.Min(cols, Cols);
        int copyRows = Math.Min(rows, Rows);
        for (int r = 0; r < copyRows; r++)
            for (int c = 0; c < copyCols; c++)
                next[r * cols + c] = _cells[r * Cols + c];
        _cells = next;
        Cols = cols;
        Rows = rows;
    }

    private void CheckBounds(int row, int col)
    {
        if ((uint)row >= (uint)Rows) throw new ArgumentOutOfRangeException(nameof(row));
        if ((uint)col >= (uint)Cols) throw new ArgumentOutOfRangeException(nameof(col));
    }
}
