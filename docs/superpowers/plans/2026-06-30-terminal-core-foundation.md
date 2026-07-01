# Terminal Core Foundation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build the headless, fully unit-testable foundation of agwinterm's terminal engine — repo scaffold, core cell/grid model, a Paul-Williams VT parser state machine, and enough sequence handling (print, C0 controls, cursor movement, SGR colors, erase) to correctly model basic shell output into a grid.

**Architecture:** A pure-C# class library (`Agwinterm.Core`) with no UI/PTY/GPU dependencies, mirroring agterm's host-free `agtermCore` boundary. A byte-stream `VtParser` (table-driven state machine) drives a `TerminalEmulator` that mutates a `ScreenBuffer` grid of `Cell`s. Everything is exercised through xUnit tests via `dotnet test` — no display required.

**Tech Stack:** .NET 10 LTS, C#, xUnit. No WinUI/Win2D/ConPTY in this plan (later phases).

## Global Constraints

- **Runtime:** .NET 10 LTS only (10.0.301). Pinned via `global.json` with `rollForward: latestFeature`. Never target STS releases (.NET 9/11).
- **LTS everywhere:** every Microsoft dependency uses its stable/LTS release; no preview/beta packages.
- **No web rendering** anywhere in the product (does not arise in this plan; constraint inherited).
- **Headless purity:** `Agwinterm.Core` references only the BCL — no `Microsoft.WindowsAppSDK`, Win2D, WinUI, or PTY packages. This is the swappable-engine boundary.
- **TDD:** every behavior is a failing test first, then minimal implementation.
- **Naming:** project root namespace `Agwinterm`; core library namespace `Agwinterm.Core`.

---

### Task 1: Repository scaffold

**Files:**
- Create: `global.json`
- Create: `.gitignore`
- Create: `Agwinterm.sln`
- Create: `src/Agwinterm.Core/Agwinterm.Core.csproj`
- Create: `tests/Agwinterm.Core.Tests/Agwinterm.Core.Tests.csproj`
- Create: `src/Agwinterm.Core/Placeholder.cs` (temporary, removed in Task 2)

**Interfaces:**
- Consumes: nothing.
- Produces: a buildable solution with a test project referencing the core library; `dotnet test` runs (zero tests).

- [ ] **Step 1: Pin the SDK** — create `global.json`:

```json
{
  "sdk": {
    "version": "10.0.301",
    "rollForward": "latestFeature"
  }
}
```

- [ ] **Step 2: Create `.gitignore`** (root):

```gitignore
bin/
obj/
*.user
.vs/
artifacts/
```

- [ ] **Step 3: Create the core library project** `src/Agwinterm.Core/Agwinterm.Core.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <RootNamespace>Agwinterm.Core</RootNamespace>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
  </PropertyGroup>
</Project>
```

- [ ] **Step 4: Add a temporary placeholder** `src/Agwinterm.Core/Placeholder.cs` so the lib compiles:

```csharp
namespace Agwinterm.Core;

internal static class Placeholder;
```

- [ ] **Step 5: Create the test project** `tests/Agwinterm.Core.Tests/Agwinterm.Core.Tests.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <IsPackable>false</IsPackable>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.11.1" />
    <PackageReference Include="xunit" Version="2.9.2" />
    <PackageReference Include="xunit.runner.visualstudio" Version="2.8.2" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="../../src/Agwinterm.Core/Agwinterm.Core.csproj" />
  </ItemGroup>
</Project>
```

- [ ] **Step 6: Create the solution and add projects**

Run:
```bash
dotnet new sln -n Agwinterm
dotnet sln add src/Agwinterm.Core/Agwinterm.Core.csproj
dotnet sln add tests/Agwinterm.Core.Tests/Agwinterm.Core.Tests.csproj
```

- [ ] **Step 7: Build and test**

Run: `dotnet build && dotnet test`
Expected: build succeeds; test run reports 0 tests passing (no failures).

- [ ] **Step 8: Commit**

```bash
git add -A
git commit -m "chore: scaffold solution pinned to .NET 10 LTS"
```

---

### Task 2: Cell, attributes, and color model

**Files:**
- Create: `src/Agwinterm.Core/Color.cs`
- Create: `src/Agwinterm.Core/CellAttributes.cs`
- Create: `src/Agwinterm.Core/Cell.cs`
- Delete: `src/Agwinterm.Core/Placeholder.cs`
- Test: `tests/Agwinterm.Core.Tests/CellTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces:
  - `readonly record struct Color(byte R, byte G, byte B)` with static `Color DefaultForeground`, `Color DefaultBackground`, and `static Color FromIndex(int paletteIndex)` (xterm 256-color palette).
  - `[Flags] enum CellAttributes { None=0, Bold=1, Italic=2, Underline=4, Inverse=8 }`.
  - `readonly record struct Cell(char Rune, Color Foreground, Color Background, CellAttributes Attributes)` with `static Cell Empty` (space, default fg/bg, no attrs).

- [ ] **Step 1: Write the failing test** `tests/Agwinterm.Core.Tests/CellTests.cs`:

```csharp
using Agwinterm.Core;

namespace Agwinterm.Core.Tests;

public class CellTests
{
    [Fact]
    public void Empty_IsSpaceWithDefaults()
    {
        var c = Cell.Empty;
        Assert.Equal(' ', c.Rune);
        Assert.Equal(Color.DefaultForeground, c.Foreground);
        Assert.Equal(Color.DefaultBackground, c.Background);
        Assert.Equal(CellAttributes.None, c.Attributes);
    }

    [Fact]
    public void FromIndex_BasicAnsiColors()
    {
        Assert.Equal(new Color(0, 0, 0), Color.FromIndex(0));       // black
        Assert.Equal(new Color(205, 0, 0), Color.FromIndex(1));     // red
        Assert.Equal(new Color(229, 229, 229), Color.FromIndex(7)); // white
    }

    [Fact]
    public void FromIndex_GrayscaleRamp()
    {
        // index 232 = first grayscale step (8,8,8)
        Assert.Equal(new Color(8, 8, 8), Color.FromIndex(232));
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --filter CellTests`
Expected: FAIL — `Color`/`Cell`/`CellAttributes` do not exist (compile error).

- [ ] **Step 3: Implement `Color.cs`**

```csharp
namespace Agwinterm.Core;

public readonly record struct Color(byte R, byte G, byte B)
{
    public static Color DefaultForeground => new(229, 229, 229);
    public static Color DefaultBackground => new(0, 0, 0);

    private static readonly byte[] StandardLevels = { 0, 95, 135, 175, 215, 255 };
    private static readonly Color[] Ansi16 =
    {
        new(0,0,0),      new(205,0,0),   new(0,205,0),   new(205,205,0),
        new(0,0,238),    new(205,0,205), new(0,205,205), new(229,229,229),
        new(127,127,127),new(255,0,0),   new(0,255,0),   new(255,255,0),
        new(92,92,255),  new(255,0,255), new(0,255,255), new(255,255,255),
    };

    public static Color FromIndex(int paletteIndex)
    {
        if (paletteIndex is < 0 or > 255)
            throw new ArgumentOutOfRangeException(nameof(paletteIndex));
        if (paletteIndex < 16)
            return Ansi16[paletteIndex];
        if (paletteIndex < 232)
        {
            int i = paletteIndex - 16;
            return new Color(StandardLevels[i / 36], StandardLevels[(i / 6) % 6], StandardLevels[i % 6]);
        }
        byte v = (byte)(8 + (paletteIndex - 232) * 10);
        return new Color(v, v, v);
    }
}
```

- [ ] **Step 4: Implement `CellAttributes.cs`**

```csharp
namespace Agwinterm.Core;

[Flags]
public enum CellAttributes
{
    None = 0,
    Bold = 1,
    Italic = 2,
    Underline = 4,
    Inverse = 8,
}
```

- [ ] **Step 5: Implement `Cell.cs`**

```csharp
namespace Agwinterm.Core;

public readonly record struct Cell(char Rune, Color Foreground, Color Background, CellAttributes Attributes)
{
    public static Cell Empty => new(' ', Color.DefaultForeground, Color.DefaultBackground, CellAttributes.None);
}
```

- [ ] **Step 6: Delete the placeholder**

Run: `rm src/Agwinterm.Core/Placeholder.cs`

- [ ] **Step 7: Run tests to verify pass**

Run: `dotnet test --filter CellTests`
Expected: PASS (3 tests).

- [ ] **Step 8: Commit**

```bash
git add -A
git commit -m "feat(core): cell, attributes, and 256-color model"
```

---

### Task 3: ScreenBuffer grid

**Files:**
- Create: `src/Agwinterm.Core/ScreenBuffer.cs`
- Test: `tests/Agwinterm.Core.Tests/ScreenBufferTests.cs`

**Interfaces:**
- Consumes: `Cell`.
- Produces: `class ScreenBuffer` with:
  - ctor `ScreenBuffer(int cols, int rows)`
  - `int Cols { get; }`, `int Rows { get; }`
  - `Cell this[int row, int col] { get; set; }` (throws `ArgumentOutOfRangeException` out of bounds)
  - `void Clear()` — fill with `Cell.Empty`
  - `void Resize(int cols, int rows)` — preserve overlapping content top-left, fill new area with `Cell.Empty`

- [ ] **Step 1: Write the failing test** `tests/Agwinterm.Core.Tests/ScreenBufferTests.cs`:

```csharp
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
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --filter ScreenBufferTests`
Expected: FAIL — `ScreenBuffer` does not exist.

- [ ] **Step 3: Implement `ScreenBuffer.cs`**

```csharp
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
```

- [ ] **Step 4: Run tests to verify pass**

Run: `dotnet test --filter ScreenBufferTests`
Expected: PASS (4 tests).

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat(core): ScreenBuffer grid with resize"
```

---

### Task 4: VT parser state machine (Paul Williams)

**Files:**
- Create: `src/Agwinterm.Core/ParserAction.cs`
- Create: `src/Agwinterm.Core/IParserPerformer.cs`
- Create: `src/Agwinterm.Core/VtParser.cs`
- Test: `tests/Agwinterm.Core.Tests/VtParserTests.cs`

**Interfaces:**
- Consumes: nothing (operates on bytes).
- Produces:
  - `interface IParserPerformer` with: `void Print(char ch)`, `void Execute(byte control)`, `void CsiDispatch(char final, IReadOnlyList<int> parameters)`, `void EscDispatch(char final)`.
  - `class VtParser` with ctor `VtParser(IParserPerformer performer)` and `void Feed(ReadOnlySpan<byte> bytes)`.
  - Implements the Williams DEC ANSI states: Ground, Escape, CsiEntry, CsiParam, CsiIntermediate, CsiIgnore. (DCS/OSC come in a later plan.)

- [ ] **Step 1: Write the failing test** `tests/Agwinterm.Core.Tests/VtParserTests.cs`:

```csharp
using System.Text;
using Agwinterm.Core;

namespace Agwinterm.Core.Tests;

public class VtParserTests
{
    private sealed class Recorder : IParserPerformer
    {
        public readonly List<string> Events = new();
        public void Print(char ch) => Events.Add($"print:{ch}");
        public void Execute(byte control) => Events.Add($"exec:{control}");
        public void CsiDispatch(char final, IReadOnlyList<int> p) =>
            Events.Add($"csi:{final}:{string.Join(',', p)}");
        public void EscDispatch(char final) => Events.Add($"esc:{final}");
    }

    private static Recorder Run(string input)
    {
        var rec = new Recorder();
        new VtParser(rec).Feed(Encoding.ASCII.GetBytes(input));
        return rec;
    }

    [Fact]
    public void PrintsPlainText()
    {
        var rec = Run("Hi");
        Assert.Equal(new[] { "print:H", "print:i" }, rec.Events);
    }

    [Fact]
    public void ExecutesC0Control()
    {
        var rec = Run("A\nB");
        Assert.Equal(new[] { "print:A", "exec:10", "print:B" }, rec.Events);
    }

    [Fact]
    public void ParsesCsiWithParameters()
    {
        var rec = Run("\x1b[1;31m");
        Assert.Equal(new[] { "csi:m:1,31" }, rec.Events);
    }

    [Fact]
    public void CsiNoParams_DispatchesEmpty()
    {
        var rec = Run("\x1b[H");
        Assert.Equal(new[] { "csi:H:" }, rec.Events);
    }

    [Fact]
    public void ParsesEscFinal()
    {
        var rec = Run("\x1bM");
        Assert.Equal(new[] { "esc:M" }, rec.Events);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --filter VtParserTests`
Expected: FAIL — `VtParser`/`IParserPerformer` do not exist.

- [ ] **Step 3: Implement `ParserAction.cs`**

```csharp
namespace Agwinterm.Core;

internal enum ParserState
{
    Ground,
    Escape,
    CsiEntry,
    CsiParam,
    CsiIntermediate,
    CsiIgnore,
}
```

- [ ] **Step 4: Implement `IParserPerformer.cs`**

```csharp
namespace Agwinterm.Core;

public interface IParserPerformer
{
    void Print(char ch);
    void Execute(byte control);
    void CsiDispatch(char final, IReadOnlyList<int> parameters);
    void EscDispatch(char final);
}
```

- [ ] **Step 5: Implement `VtParser.cs`**

```csharp
namespace Agwinterm.Core;

public sealed class VtParser(IParserPerformer performer)
{
    private ParserState _state = ParserState.Ground;
    private readonly List<int> _params = new();
    private int _current;
    private bool _hasCurrent;

    public void Feed(ReadOnlySpan<byte> bytes)
    {
        foreach (byte b in bytes)
            Step(b);
    }

    private void Step(byte b)
    {
        // C0 controls (except ESC) execute from any state and return to it,
        // except inside the simple paths we model here they execute in Ground.
        if (b == 0x1b) { EnterEscape(); return; }

        switch (_state)
        {
            case ParserState.Ground:
                if (IsControl(b)) performer.Execute(b);
                else performer.Print((char)b);
                break;

            case ParserState.Escape:
                if (b == (byte)'[') { _state = ParserState.CsiEntry; ResetParams(); }
                else if (b is >= 0x30 and <= 0x7e) { performer.EscDispatch((char)b); _state = ParserState.Ground; }
                else if (IsControl(b)) performer.Execute(b);
                else _state = ParserState.Ground;
                break;

            case ParserState.CsiEntry:
            case ParserState.CsiParam:
                if (b is >= (byte)'0' and <= (byte)'9') { _current = _current * 10 + (b - '0'); _hasCurrent = true; _state = ParserState.CsiParam; }
                else if (b == (byte)';') { PushParam(); _state = ParserState.CsiParam; }
                else if (b is >= 0x40 and <= 0x7e) { PushParamIfAny(); performer.CsiDispatch((char)b, _params); _state = ParserState.Ground; }
                else if (b is >= 0x20 and <= 0x2f) { _state = ParserState.CsiIntermediate; }
                else if (IsControl(b)) performer.Execute(b);
                else _state = ParserState.CsiIgnore;
                break;

            case ParserState.CsiIntermediate:
                if (b is >= 0x40 and <= 0x7e) { PushParamIfAny(); performer.CsiDispatch((char)b, _params); _state = ParserState.Ground; }
                else if (b is >= 0x20 and <= 0x2f) { /* collect intermediates: ignored for now */ }
                else _state = ParserState.CsiIgnore;
                break;

            case ParserState.CsiIgnore:
                if (b is >= 0x40 and <= 0x7e) _state = ParserState.Ground;
                break;
        }
    }

    private void EnterEscape()
    {
        _state = ParserState.Escape;
        ResetParams();
    }

    private static bool IsControl(byte b) => b < 0x20 || b == 0x7f;

    private void ResetParams()
    {
        _params.Clear();
        _current = 0;
        _hasCurrent = false;
    }

    private void PushParam()
    {
        _params.Add(_hasCurrent ? _current : 0);
        _current = 0;
        _hasCurrent = false;
    }

    private void PushParamIfAny()
    {
        if (_hasCurrent || _params.Count > 0)
            _params.Add(_hasCurrent ? _current : 0);
    }
}
```

- [ ] **Step 6: Run tests to verify pass**

Run: `dotnet test --filter VtParserTests`
Expected: PASS (5 tests).

- [ ] **Step 7: Commit**

```bash
git add -A
git commit -m "feat(core): Williams VT parser state machine (print/exec/CSI/ESC)"
```

---

### Task 5: TerminalEmulator — print, cursor, and C0 controls

**Files:**
- Create: `src/Agwinterm.Core/TerminalEmulator.cs`
- Test: `tests/Agwinterm.Core.Tests/TerminalEmulatorTests.cs`

**Interfaces:**
- Consumes: `ScreenBuffer`, `Cell`, `VtParser`, `IParserPerformer`.
- Produces: `class TerminalEmulator : IParserPerformer` with:
  - ctor `TerminalEmulator(int cols, int rows)`
  - `ScreenBuffer Screen { get; }`
  - `int CursorRow { get; }`, `int CursorCol { get; }`
  - `void Feed(ReadOnlySpan<byte> bytes)` (delegates to an internal `VtParser`)
  - `string DumpRow(int row)` — returns the row's runes as a trimmed string (test helper)
  - Behavior: `Print` writes at cursor and advances (wraps to next line at right edge); `\r` (13) → col 0; `\n` (10) → next row (scrolls if at bottom); `\b` (8) → col-1 (min 0); `\t` (9) → next multiple-of-8 column.

- [ ] **Step 1: Write the failing test** `tests/Agwinterm.Core.Tests/TerminalEmulatorTests.cs`:

```csharp
using System.Text;
using Agwinterm.Core;

namespace Agwinterm.Core.Tests;

public class TerminalEmulatorTests
{
    private static TerminalEmulator Feed(int cols, int rows, string s)
    {
        var t = new TerminalEmulator(cols, rows);
        t.Feed(Encoding.ASCII.GetBytes(s));
        return t;
    }

    [Fact]
    public void PrintsTextAndAdvancesCursor()
    {
        var t = Feed(10, 2, "abc");
        Assert.Equal("abc", t.DumpRow(0));
        Assert.Equal(0, t.CursorRow);
        Assert.Equal(3, t.CursorCol);
    }

    [Fact]
    public void CarriageReturnAndLineFeed()
    {
        var t = Feed(10, 3, "ab\r\nc");
        Assert.Equal("ab", t.DumpRow(0));
        Assert.Equal("c", t.DumpRow(1));
        Assert.Equal(1, t.CursorRow);
        Assert.Equal(1, t.CursorCol);
    }

    [Fact]
    public void Backspace()
    {
        var t = Feed(10, 2, "ab\bc");
        Assert.Equal("ac", t.DumpRow(0)); // c overwrites b
        Assert.Equal(2, t.CursorCol);
    }

    [Fact]
    public void Tab_AdvancesToNextMultipleOfEight()
    {
        var t = Feed(20, 2, "a\tb");
        Assert.Equal(0, t.CursorRow);
        Assert.Equal(9, t.CursorCol); // tab from col1 -> col8, then 'b' -> col9
        Assert.Equal('b', t.Screen[0, 8].Rune);
    }

    [Fact]
    public void LineFeedAtBottom_Scrolls()
    {
        var t = Feed(10, 2, "x\r\ny\r\nz");
        Assert.Equal("y", t.DumpRow(0)); // scrolled up
        Assert.Equal("z", t.DumpRow(1));
        Assert.Equal(1, t.CursorRow);
    }

    [Fact]
    public void PrintWrapsAtRightEdge()
    {
        var t = Feed(3, 2, "abcd");
        Assert.Equal("abc", t.DumpRow(0));
        Assert.Equal("d", t.DumpRow(1));
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --filter TerminalEmulatorTests`
Expected: FAIL — `TerminalEmulator` does not exist.

- [ ] **Step 3: Implement `TerminalEmulator.cs`**

```csharp
namespace Agwinterm.Core;

public sealed class TerminalEmulator : IParserPerformer
{
    private readonly VtParser _parser;

    public ScreenBuffer Screen { get; }
    public int CursorRow { get; private set; }
    public int CursorCol { get; private set; }

    public TerminalEmulator(int cols, int rows)
    {
        Screen = new ScreenBuffer(cols, rows);
        _parser = new VtParser(this);
    }

    public void Feed(ReadOnlySpan<byte> bytes) => _parser.Feed(bytes);

    public void Print(char ch)
    {
        if (CursorCol >= Screen.Cols)
        {
            CursorCol = 0;
            NextLine();
        }
        Screen[CursorRow, CursorCol] = Cell.Empty with { Rune = ch };
        CursorCol++;
    }

    public void Execute(byte control)
    {
        switch (control)
        {
            case 13: CursorCol = 0; break;                       // CR
            case 10: NextLine(); break;                          // LF
            case 8: if (CursorCol > 0) CursorCol--; break;       // BS
            case 9:                                              // HT
                CursorCol = Math.Min(Screen.Cols - 1, ((CursorCol / 8) + 1) * 8);
                break;
        }
    }

    // CSI / ESC handled in a later task.
    public void CsiDispatch(char final, IReadOnlyList<int> parameters) { }
    public void EscDispatch(char final) { }

    private void NextLine()
    {
        if (CursorRow < Screen.Rows - 1)
            CursorRow++;
        else
            ScrollUp();
    }

    private void ScrollUp()
    {
        for (int r = 1; r < Screen.Rows; r++)
            for (int c = 0; c < Screen.Cols; c++)
                Screen[r - 1, c] = Screen[r, c];
        for (int c = 0; c < Screen.Cols; c++)
            Screen[Screen.Rows - 1, c] = Cell.Empty;
    }

    public string DumpRow(int row)
    {
        var sb = new System.Text.StringBuilder();
        for (int c = 0; c < Screen.Cols; c++)
            sb.Append(Screen[row, c].Rune);
        return sb.ToString().TrimEnd();
    }
}
```

- [ ] **Step 4: Run tests to verify pass**

Run: `dotnet test --filter TerminalEmulatorTests`
Expected: PASS (6 tests).

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat(core): terminal emulator print/cursor/C0 with scroll + wrap"
```

---

### Task 6: CSI cursor movement and erase

**Files:**
- Modify: `src/Agwinterm.Core/TerminalEmulator.cs` (implement `CsiDispatch`)
- Test: `tests/Agwinterm.Core.Tests/CsiMovementTests.cs`

**Interfaces:**
- Consumes: existing `TerminalEmulator`.
- Produces: `CsiDispatch` handling — CUP (`H`), CUU/CUD/CUF/CUB (`A`/`B`/`C`/`D`), ED (`J`: 0=below,1=above,2=all), EL (`K`: 0=right,1=left,2=line). Parameters default to 1 for movement, 0 for erase; positions are 1-based in the protocol, clamped to bounds.

- [ ] **Step 1: Write the failing test** `tests/Agwinterm.Core.Tests/CsiMovementTests.cs`:

```csharp
using System.Text;
using Agwinterm.Core;

namespace Agwinterm.Core.Tests;

public class CsiMovementTests
{
    private static TerminalEmulator Feed(int cols, int rows, string s)
    {
        var t = new TerminalEmulator(cols, rows);
        t.Feed(Encoding.ASCII.GetBytes(s));
        return t;
    }

    [Fact]
    public void Cup_MovesCursorOneBased()
    {
        var t = Feed(10, 5, "\x1b[3;4H");
        Assert.Equal(2, t.CursorRow); // row 3 (1-based) -> index 2
        Assert.Equal(3, t.CursorCol);
    }

    [Fact]
    public void Cup_NoParams_GoesHome()
    {
        var t = Feed(10, 5, "abc\x1b[H");
        Assert.Equal(0, t.CursorRow);
        Assert.Equal(0, t.CursorCol);
    }

    [Fact]
    public void CursorForwardAndUp()
    {
        var t = Feed(10, 5, "\x1b[2;2H\x1b[3C\x1b[1A");
        Assert.Equal(0, t.CursorRow); // row2 -1
        Assert.Equal(4, t.CursorCol); // col2 +3 (col index 1 +3 = 4)
    }

    [Fact]
    public void EraseLineToRight()
    {
        var t = Feed(10, 2, "abcdef\x1b[3G\x1b[0K");
        Assert.Equal("ab", t.DumpRow(0)); // from col3 to end cleared
    }

    [Fact]
    public void EraseDisplayAll()
    {
        var t = Feed(10, 2, "abc\r\ndef\x1b[2J");
        Assert.Equal("", t.DumpRow(0));
        Assert.Equal("", t.DumpRow(1));
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --filter CsiMovementTests`
Expected: FAIL — `CsiDispatch` is a no-op.

- [ ] **Step 3: Implement `CsiDispatch`** — replace the empty method in `TerminalEmulator.cs`:

```csharp
    public void CsiDispatch(char final, IReadOnlyList<int> parameters)
    {
        int P(int index, int def) =>
            index < parameters.Count && parameters[index] != 0 ? parameters[index] : def;

        switch (final)
        {
            case 'H': // CUP (also 'f')
            case 'f':
                CursorRow = Math.Clamp(P(0, 1) - 1, 0, Screen.Rows - 1);
                CursorCol = Math.Clamp(P(1, 1) - 1, 0, Screen.Cols - 1);
                break;
            case 'A': CursorRow = Math.Max(0, CursorRow - P(0, 1)); break;
            case 'B': CursorRow = Math.Min(Screen.Rows - 1, CursorRow + P(0, 1)); break;
            case 'C': CursorCol = Math.Min(Screen.Cols - 1, CursorCol + P(0, 1)); break;
            case 'D': CursorCol = Math.Max(0, CursorCol - P(0, 1)); break;
            case 'G': CursorCol = Math.Clamp(P(0, 1) - 1, 0, Screen.Cols - 1); break;
            case 'J': EraseDisplay(parameters.Count > 0 ? parameters[0] : 0); break;
            case 'K': EraseLine(parameters.Count > 0 ? parameters[0] : 0); break;
        }
    }

    private void EraseLine(int mode)
    {
        int from = mode == 0 ? CursorCol : 0;
        int to = mode == 1 ? CursorCol : Screen.Cols - 1;
        for (int c = from; c <= to; c++) Screen[CursorRow, c] = Cell.Empty;
    }

    private void EraseDisplay(int mode)
    {
        if (mode == 2) { Screen.Clear(); return; }
        if (mode == 0)
        {
            EraseLine(0);
            for (int r = CursorRow + 1; r < Screen.Rows; r++)
                for (int c = 0; c < Screen.Cols; c++) Screen[r, c] = Cell.Empty;
        }
        else if (mode == 1)
        {
            EraseLine(1);
            for (int r = 0; r < CursorRow; r++)
                for (int c = 0; c < Screen.Cols; c++) Screen[r, c] = Cell.Empty;
        }
    }
```

- [ ] **Step 4: Run tests to verify pass**

Run: `dotnet test --filter CsiMovementTests`
Expected: PASS (5 tests). Also run full suite: `dotnet test` — all green.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat(core): CSI cursor movement and erase (CUP/CUx/ED/EL)"
```

---

### Task 7: SGR attributes (colors and styles)

**Files:**
- Modify: `src/Agwinterm.Core/TerminalEmulator.cs` (track current pen; handle SGR `m`; apply pen on `Print`)
- Test: `tests/Agwinterm.Core.Tests/SgrTests.cs`

**Interfaces:**
- Consumes: existing `TerminalEmulator`, `Cell`, `Color`, `CellAttributes`.
- Produces: SGR handling on `CsiDispatch` final `'m'`: 0=reset, 1=bold, 3=italic, 4=underline, 7=inverse, 30-37 fg, 40-47 bg, 90-97/100-107 bright, 38;5;n / 48;5;n indexed, 38;2;r;g;b / 48;2;r;g;b truecolor, 39/49 default fg/bg. Printed cells carry the current pen.

- [ ] **Step 1: Write the failing test** `tests/Agwinterm.Core.Tests/SgrTests.cs`:

```csharp
using System.Text;
using Agwinterm.Core;

namespace Agwinterm.Core.Tests;

public class SgrTests
{
    private static TerminalEmulator Feed(string s)
    {
        var t = new TerminalEmulator(20, 3);
        t.Feed(Encoding.ASCII.GetBytes(s));
        return t;
    }

    [Fact]
    public void BoldRedForeground()
    {
        var t = Feed("\x1b[1;31mX");
        var cell = t.Screen[0, 0];
        Assert.Equal('X', cell.Rune);
        Assert.True(cell.Attributes.HasFlag(CellAttributes.Bold));
        Assert.Equal(Color.FromIndex(1), cell.Foreground);
    }

    [Fact]
    public void ResetClearsPen()
    {
        var t = Feed("\x1b[31mA\x1b[0mB");
        Assert.Equal(Color.FromIndex(1), t.Screen[0, 0].Foreground);
        Assert.Equal(Color.DefaultForeground, t.Screen[0, 1].Foreground);
    }

    [Fact]
    public void TrueColorForeground()
    {
        var t = Feed("\x1b[38;2;10;20;30mZ");
        Assert.Equal(new Color(10, 20, 30), t.Screen[0, 0].Foreground);
    }

    [Fact]
    public void IndexedBackground()
    {
        var t = Feed("\x1b[48;5;4mZ");
        Assert.Equal(Color.FromIndex(4), t.Screen[0, 0].Background);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --filter SgrTests`
Expected: FAIL — pen not applied.

- [ ] **Step 3: Implement SGR** — add pen fields and apply them. In `TerminalEmulator.cs`:

Add fields near the top of the class:
```csharp
    private Color _fg = Color.DefaultForeground;
    private Color _bg = Color.DefaultBackground;
    private CellAttributes _attrs = CellAttributes.None;
```

Change `Print` to stamp the pen:
```csharp
        Screen[CursorRow, CursorCol] = new Cell(ch, _fg, _bg, _attrs);
```

Add `case 'm': ApplySgr(parameters); break;` to the `CsiDispatch` switch, and add:
```csharp
    private void ApplySgr(IReadOnlyList<int> p)
    {
        if (p.Count == 0) { ResetPen(); return; }
        for (int i = 0; i < p.Count; i++)
        {
            int code = p[i];
            switch (code)
            {
                case 0: ResetPen(); break;
                case 1: _attrs |= CellAttributes.Bold; break;
                case 3: _attrs |= CellAttributes.Italic; break;
                case 4: _attrs |= CellAttributes.Underline; break;
                case 7: _attrs |= CellAttributes.Inverse; break;
                case >= 30 and <= 37: _fg = Color.FromIndex(code - 30); break;
                case 39: _fg = Color.DefaultForeground; break;
                case >= 40 and <= 47: _bg = Color.FromIndex(code - 40); break;
                case 49: _bg = Color.DefaultBackground; break;
                case >= 90 and <= 97: _fg = Color.FromIndex(code - 90 + 8); break;
                case >= 100 and <= 107: _bg = Color.FromIndex(code - 100 + 8); break;
                case 38: i = ExtendedColor(p, i, ref _fg); break;
                case 48: i = ExtendedColor(p, i, ref _bg); break;
            }
        }
    }

    private static int ExtendedColor(IReadOnlyList<int> p, int i, ref Color target)
    {
        if (i + 1 >= p.Count) return i;
        int mode = p[i + 1];
        if (mode == 5 && i + 2 < p.Count) { target = Color.FromIndex(p[i + 2]); return i + 2; }
        if (mode == 2 && i + 4 < p.Count) { target = new Color((byte)p[i + 2], (byte)p[i + 3], (byte)p[i + 4]); return i + 4; }
        return i + 1;
    }

    private void ResetPen()
    {
        _fg = Color.DefaultForeground;
        _bg = Color.DefaultBackground;
        _attrs = CellAttributes.None;
    }
```

- [ ] **Step 4: Run tests to verify pass**

Run: `dotnet test --filter SgrTests`
Expected: PASS (4 tests). Then `dotnet test` — full suite green.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat(core): SGR attributes — 16/256/truecolor and styles"
```

---

## Self-Review

**Spec coverage (this plan's slice):** scaffold (LTS-pinned) ✓; cell/color model ✓; grid + resize ✓; VT state machine ✓; print/cursor/C0 + scroll/wrap ✓; CSI movement + erase ✓; SGR colors/styles ✓. Out of this plan's slice (tracked for later plans): UTF-8 multibyte decode, OSC/DCS, Kitty graphics APC, alt-screen, scroll regions, scrollback buffer, `ITerminalEngine` façade, ConPTY layer, Win2D renderer, WinUI shell, `agtermCore` port, control API, agent hooks.

**Placeholder scan:** no TBD/TODO; every code step shows complete code; commands have expected output. ✓

**Type consistency:** `IParserPerformer` signatures (`Print(char)`, `Execute(byte)`, `CsiDispatch(char, IReadOnlyList<int>)`, `EscDispatch(char)`) are identical across Tasks 4–7; `Cell` ctor `(Rune, Foreground, Background, Attributes)` used consistently; `Color.FromIndex(int)` and `Color.Default*` consistent. ✓

---

## Subsequent plans (roadmap, not part of this plan)

1. **terminal-core-completion** — UTF-8 decode, wide chars (Wcwidth), OSC 0/2/7/8, DCS, alt-screen, scroll regions, scrollback, more CSI; `ITerminalEngine` façade.
2. **kitty-graphics-core** — APC `_G` parsing + image placement model (port from SwiftTerm).
3. **conpty-layer** — Porta.Pty integration, per-session reader task, resize; headless spawn tests.
4. **win2d-renderer** — SwapChainPanel + glyph atlas text pass (needs visual verification).
5. **kitty-graphics-renderer** — GPU texture cache by image ID + compositing pass + benchmark gate.
6. **app-layer** — port `agtermCore` (tree, persistence, multi-window).
7. **control-api-and-hooks** — named-pipe server, `agwintermctl`, PowerShell hook installer, agent skill.
8. **parity-features** — splits, overlays, search, switcher, palette, keymap.
9. **polish** — toasts, sounds, restoration, theming; optional Sixel.
