using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Agwinterm.Core;
using Agwinterm.Pty;
using SharpGen.Runtime;
using Vortice.Direct2D1;
using Vortice.DirectWrite;
using Vortice.DCommon;
using Vortice.Mathematics;
using static Agwinterm.Win32.Win32;
using Color = Agwinterm.Core.Color;

namespace Agwinterm.Win32;

/// <summary>Rendering: Kitty images + the Direct2D terminal draw path.</summary>
internal partial class Program
{
    // ---- Kitty images (Direct2D) ----

    private static readonly string? _imgLog = Environment.GetEnvironmentVariable("AGWINTERM_IMGLOG");
    private static void Log(string m) { if (_imgLog is not null) try { File.AppendAllText(_imgLog, m + "\n"); } catch { } }

    private static readonly string? _perfLog = Environment.GetEnvironmentVariable("AGWINTERM_PERF");
    private static void Perf(string m) { if (_perfLog is not null) try { File.AppendAllText(_perfLog, m + "\n"); } catch { } }
    private int _uploadCount;
    private double _uploadMs;

    /// <summary>
    /// Draw current image placements. Decoding (PNG decompress) happens on a background
    /// thread so the UI never blocks; only the cheap GPU upload runs here. An image simply
    /// appears on the next redraw once its pixels are ready. Called under the session lock.
    /// </summary>
    private void DrawImages(TerminalEmulator em, float ox, float oy, float cw, float ch)
    {
        if (_rt is null) return;

        // 1) Upload any pixels decoded on background threads (cheap; UI thread only).
        while (_decoded.TryDequeue(out var d))
        {
            _decoding.Remove(d.img);
            if (d.bgra is null || _imageCache.ContainsKey(d.img)) continue;
            try
            {
                long t0 = Stopwatch.GetTimestamp();
                var handle = GCHandle.Alloc(d.bgra, GCHandleType.Pinned);
                try { _imageCache[d.img] = _rt.CreateBitmap(new SizeI(d.w, d.h), handle.AddrOfPinnedObject(), (uint)(d.w * 4), _bmpProps); }
                finally { handle.Free(); }
                _uploadCount++; _uploadMs += Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
                Log($"uploaded id={d.img.Id} {d.w}x{d.h}");
            }
            catch (Exception ex) { Log($"upload FAILED id={d.img.Id}: {ex.GetType().Name} {ex.Message}"); }
        }

        // 2) Prune textures whose image was retransmitted or deleted (bounds memory during scroll).
        if (_imageCache.Count > 0)
        {
            var live = new HashSet<KittyImage>(em.Images.Values);
            foreach (var stale in _imageCache.Keys.Where(k => !live.Contains(k)).ToList())
            {
                _imageCache[stale].Dispose();
                _imageCache.Remove(stale);
            }
        }

        // 3) Draw what's ready; kick off background decodes for what isn't.
        foreach (var p in em.Placements)
        {
            if (!em.Images.TryGetValue(p.ImageId, out var img)) continue;
            if (!_imageCache.TryGetValue(img, out var bmp))
            {
                if (_decoding.Add(img)) // not already decoding
                    _ = Task.Run(() => DecodePixelsAsync(img));
                continue; // will render on a later frame once uploaded
            }
            float ix = ox + p.Col * cw;
            float iy = oy + p.Row * ch;
            float iw = p.Cols > 0 ? p.Cols * cw : bmp.Size.Width;
            float ih = p.Rows > 0 ? p.Rows * ch : bmp.Size.Height;
            var dest = new Vortice.RawRectF(ix, iy, ix + iw, iy + ih);
            // Optional pixel source crop: scrolling just moves this window over the cached texture
            // (no re-transmit/re-decode/re-upload). null = whole image.
            Vortice.RawRectF? src = (p.SrcW > 0 && p.SrcH > 0)
                ? new Vortice.RawRectF(p.SrcX, p.SrcY, p.SrcX + p.SrcW, p.SrcY + p.SrcH)
                : null;
            try { _rt.DrawBitmap(bmp, dest, 1f, BitmapInterpolationMode.Linear, src); }
            catch (Exception ex) { Log($"DrawBitmap FAILED: {ex.GetType().Name} {ex.Message}"); }
        }
    }

    private static readonly BitmapProperties _bmpProps =
        new(new PixelFormat(Vortice.DXGI.Format.B8G8R8A8_UNorm, AlphaMode.Premultiplied), 96f, 96f);

    /// <summary>
    /// Draw a session's background watermark into a pane's content rect (behind the cells; text is
    /// painted on top so it stays readable). Decode is off the UI thread (like DrawImages); the
    /// image simply appears once ready. Scrollback doesn't move it — it's pinned to the pane.
    /// </summary>
    private void DrawWatermark(Ses ses, float ox, float oy, float pw, float ph)
    {
        if (_rt is null || string.IsNullOrEmpty(ses.BgPath) || ses.BgOpacity <= 0 || pw <= 1 || ph <= 1) return;
        string path = ses.BgPath!;

        // Upload anything decoded on background threads (cheap; UI thread only).
        while (_bgDecoded.TryDequeue(out var d))
        {
            _bgDecoding.Remove(d.path);
            if (d.bgra is null || _bgCache.ContainsKey(d.path)) continue;
            try
            {
                var h = GCHandle.Alloc(d.bgra, GCHandleType.Pinned);
                try { _bgCache[d.path] = _rt.CreateBitmap(new SizeI(d.w, d.h), h.AddrOfPinnedObject(), (uint)(d.w * 4), _bmpProps); }
                finally { h.Free(); }
            }
            catch (Exception ex) { Log($"watermark upload FAILED {path}: {ex.Message}"); }
        }

        if (!_bgCache.TryGetValue(path, out var bmp))
        {
            if (_bgDecoding.Add(path)) _ = Task.Run(() => DecodeBackgroundAsync(path));
            return; // renders on a later frame once uploaded
        }

        float iw = bmp.Size.Width, ih = bmp.Size.Height;
        if (iw < 1 || ih < 1) return;
        float opacity = System.Math.Clamp(ses.BgOpacity, 0, 100) / 100f;

        _rt.PushAxisAlignedClip(new Vortice.RawRectF(ox, oy, ox + pw, oy + ph), AntialiasMode.Aliased);
        try
        {
            switch (ses.BgMode)
            {
                case "tile":
                    for (float ty = oy; ty < oy + ph; ty += ih)
                        for (float tx = ox; tx < ox + pw; tx += iw)
                            _rt.DrawBitmap(bmp, new Vortice.RawRectF(tx, ty, tx + iw, ty + ih), opacity, BitmapInterpolationMode.Linear, null);
                    break;
                case "center":
                {
                    float cx = ox + (pw - iw) / 2f, cy = oy + (ph - ih) / 2f;
                    _rt.DrawBitmap(bmp, new Vortice.RawRectF(cx, cy, cx + iw, cy + ih), opacity, BitmapInterpolationMode.Linear, null);
                    break;
                }
                default: // "fit" (letterbox) and "fill" (cover) — same math, min vs max scale; clip handles overflow.
                {
                    float scale = ses.BgMode == "fill" ? MathF.Max(pw / iw, ph / ih) : MathF.Min(pw / iw, ph / ih);
                    float dw = iw * scale, dh = ih * scale;
                    float dx = ox + (pw - dw) / 2f, dy = oy + (ph - dh) / 2f;
                    _rt.DrawBitmap(bmp, new Vortice.RawRectF(dx, dy, dx + dw, dy + dh), opacity, BitmapInterpolationMode.Linear, null);
                    break;
                }
            }
        }
        catch (Exception ex) { Log($"watermark draw FAILED {path}: {ex.Message}"); }
        finally { _rt.PopAxisAlignedClip(); }
    }

    /// <summary>Background: decode a watermark image file to premultiplied BGRA, enqueue for UI upload.</summary>
    private void DecodeBackgroundAsync(string path)
    {
        try
        {
            using var gdi = new System.Drawing.Bitmap(path); // PNG/JPG/BMP/GIF via GDI+
            int w = gdi.Width, h = gdi.Height;
            var data = gdi.LockBits(new System.Drawing.Rectangle(0, 0, w, h),
                System.Drawing.Imaging.ImageLockMode.ReadOnly,
                System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
            byte[] buf;
            try
            {
                buf = new byte[w * 4 * h];
                if (data.Stride == w * 4) Marshal.Copy(data.Scan0, buf, 0, buf.Length);
                else for (int y = 0; y < h; y++) Marshal.Copy(data.Scan0 + y * data.Stride, buf, y * w * 4, w * 4);
            }
            finally { gdi.UnlockBits(data); }
            _bgDecoded.Enqueue((path, buf, w, h));
        }
        catch (Exception ex)
        {
            Log($"watermark decode FAILED {path}: {ex.Message}");
            _bgDecoded.Enqueue((path, null, 0, 0)); // stop retrying
        }
        finally { RequestRedraw(); }
    }

    /// <summary>Drop a cached watermark bitmap (after clear/replace) so the file handle/texture is freed.</summary>
    private void EvictWatermark(string? path)
    {
        if (string.IsNullOrEmpty(path)) return;
        if (_bgCache.Remove(path!, out var bmp)) { try { bmp.Dispose(); } catch { } }
        _bgDecoding.Remove(path!);
    }

    /// <summary>Background: decode to premultiplied BGRA pixels (no D2D), enqueue for UI upload, ask for a redraw.</summary>
    private void DecodePixelsAsync(KittyImage img)
    {
        try
        {
            var (bgra, w, h) = DecodePixels(img);
            _decoded.Enqueue((img, bgra, w, h));
        }
        catch (Exception ex)
        {
            Log($"decode FAILED id={img.Id} fmt={img.Format}: {ex.GetType().Name} {ex.Message}");
            _decoded.Enqueue((img, null, 0, 0)); // signal failure so we stop retrying
        }
        finally { RequestRedraw(); }
    }

    /// <summary>Thread-safe decode (PNG via System.Drawing, or raw RGB/RGBA) to a premultiplied-BGRA buffer.</summary>
    private static (byte[] bgra, int w, int h) DecodePixels(KittyImage img)
    {
        if (img.Format == KittyFormat.Png)
        {
            using var ms = new MemoryStream(img.Data);
            using var gdi = new System.Drawing.Bitmap(ms);
            int w = gdi.Width, h = gdi.Height;
            var data = gdi.LockBits(
                new System.Drawing.Rectangle(0, 0, w, h),
                System.Drawing.Imaging.ImageLockMode.ReadOnly,
                System.Drawing.Imaging.PixelFormat.Format32bppPArgb); // premultiplied BGRA, stride == w*4 for 32bpp
            try
            {
                var buf = new byte[w * 4 * h];
                if (data.Stride == w * 4)
                    Marshal.Copy(data.Scan0, buf, 0, buf.Length);
                else // defensive: copy row by row if padded
                    for (int y = 0; y < h; y++)
                        Marshal.Copy(data.Scan0 + y * data.Stride, buf, y * w * 4, w * 4);
                return (buf, w, h);
            }
            finally { gdi.UnlockBits(data); }
        }

        return (ToPremultipliedBgra(img), img.Width, img.Height);
    }

    private static byte[] ToPremultipliedBgra(KittyImage img)
    {
        var d = img.Data;
        var outb = new byte[img.Width * img.Height * 4];
        if (img.Format == KittyFormat.Rgba)
        {
            for (int i = 0, j = 0; i + 3 < d.Length && j + 3 < outb.Length; i += 4, j += 4)
            {
                byte r = d[i], g = d[i + 1], b = d[i + 2], a = d[i + 3];
                outb[j] = (byte)(b * a / 255); outb[j + 1] = (byte)(g * a / 255);
                outb[j + 2] = (byte)(r * a / 255); outb[j + 3] = a;
            }
        }
        else // Rgb: opaque
        {
            for (int i = 0, j = 0; i + 2 < d.Length && j + 3 < outb.Length; i += 3, j += 4)
            {
                outb[j] = d[i + 2]; outb[j + 1] = d[i + 1]; outb[j + 2] = d[i]; outb[j + 3] = 255;
            }
        }
        return outb;
    }

    /// <summary>Dispose the device-bound render target + textures and rebuild them (after a device/GPU reset).</summary>
    private void RecreateTarget()
    {
        foreach (var b in _imageCache.Values) { try { b.Dispose(); } catch { } }
        _imageCache.Clear();
        _decoding.Clear();
        while (_decoded.TryDequeue(out _)) { }
        foreach (var b in _bgCache.Values) { try { b.Dispose(); } catch { } } // watermark textures are device-bound too
        _bgCache.Clear();
        _bgDecoding.Clear();
        while (_bgDecoded.TryDequeue(out _)) { }
        try { _brush?.Dispose(); } catch { }
        try { _rt?.Dispose(); } catch { }
        _brush = null; _rt = null;
        try { CreateRenderTarget(); } catch (Exception ex) { Perf($"recreate FAILED: {ex.Message}"); }
        InvalidateRect(_hwnd, IntPtr.Zero, false);
    }

    // D2DERR_RECREATE_TARGET: the device is lost and every device-bound resource must be rebuilt.
    private const int D2DERR_RECREATE_TARGET = unchecked((int)0x8899000C);

    private void Render()
    {
        if (_rt is null || _brush is null) return;
        long tStart = Stopwatch.GetTimestamp();
        _uploadCount = 0; _uploadMs = 0;
        try
        {
            RenderBody();
            Result end = _rt.EndDraw(out _, out _);
            if (end.Failure)
            {
                if (end.Code == D2DERR_RECREATE_TARGET) { RecreateTarget(); return; }
                Perf($"EndDraw fail 0x{end.Code:X8}");
            }
        }
        catch (SharpGenException ex)
        {
            // Device loss can surface as an exception from any draw call; rebuild and move on.
            if (ex.ResultCode.Code == D2DERR_RECREATE_TARGET) { RecreateTarget(); return; }
            Perf($"render ex {ex.ResultCode.Code:X8}: {ex.Message}");
            RecreateTarget();
            return;
        }
        catch (Exception ex) { Perf($"render ex: {ex.GetType().Name} {ex.Message}"); RecreateTarget(); return; }

        if (_perfLog is not null)
            Perf($"frame render={Stopwatch.GetElapsedTime(tStart).TotalMilliseconds:F2}ms uploads={_uploadCount} uploadMs={_uploadMs:F2} t={DateTime.Now:HH:mm:ss.fff}");
    }

    /// <summary>Draw one terminal surface (cells, images, cursor) at origin (ox,oy) with its font metrics.</summary>
    private Cell[] _cellSnap = Array.Empty<Cell>();   // reusable viewport snapshot; drawing reads it lock-free

    private void RenderTerminal(ISession session, float ox, float oy, IDWriteTextFormat fmt, float cw, float ch, int scrollOffset, Pane? selPane = null)
    {
        var rt = _rt!;
        var brush = _brush!;
        // Snapshot the visible viewport under the session lock (sub-ms), then draw OUTSIDE it.
        // Holding the lock for the whole frame serializes rendering against the PTY pump's Feed,
        // capping sustained-output throughput (dotnet-stack showed the UI thread in Monitor.Enter
        // here while the pump held the lock — see docs/memory-profile-2026-07-09.md).
        var em = session.Emulator;
        int cols, rows, hist, off;
        bool cursorVisible; int cursorCol, cursorRow;
        bool drawImages;
        (int PromptLine, int? ExitCode)[] marks;
        lock (session.SyncRoot)
        {
            em.CellPixelWidth = Math.Max(1, (int)cw); em.CellPixelHeight = Math.Max(1, (int)ch);  // for sixel cell-span
            var screen = em.Screen;
            cols = screen.Cols; rows = screen.Rows;
            hist = em.HistoryCount;
            bool isAlt = em.IsAltScreen;
            off = isAlt ? 0 : Math.Clamp(scrollOffset, 0, hist);
            cursorVisible = em.CursorVisible; cursorCol = em.CursorCol; cursorRow = em.CursorRow;
            drawImages = !_noImages && em.Placements.Count > 0 && off == 0;
            marks = !isAlt && em.Marks.Count > 0
                ? em.Marks.Select(m => (m.PromptLine, m.ExitCode)).ToArray()
                : Array.Empty<(int, int?)>();
            if (_cellSnap.Length < rows * cols) _cellSnap = new Cell[rows * cols];
            for (int r = 0; r < rows; r++)
                for (int c = 0; c < cols; c++)
                {
                    // Visible row r maps into [history ++ live grid], shifted up by `off`.
                    Cell v;
                    if (off <= 0) v = screen[r, c];
                    else
                    {
                        int vi = hist - off + r;
                        if (vi < 0) v = Cell.Empty;
                        else if (vi < hist) v = em.GetHistoryCell(vi, c);
                        else { int live = vi - hist; v = live < rows ? screen[live, c] : Cell.Empty; }
                    }
                    _cellSnap[r * cols + c] = v;
                }
        }
        // Drawing below reads only the snapshot + UI-thread state — no session lock held.
        {
            var snap = _cellSnap;
            Cell CellAt(int r, int c) => snap[r * cols + c];
            bool hasSel = selPane is { HasSel: true };
            int sl0 = 0, sc0 = 0, sl1 = 0, sc1 = 0;
            if (hasSel) NormSel(selPane!, out sl0, out sc0, out sl1, out sc1);
            bool searchHere = _searchActive && selPane is not null && ReferenceEquals(selPane, ActiveSurface());
            for (int r = 0; r < rows; r++)
            {
                float y = oy + r * ch;
                int c = 0;
                while (c < cols)  // Pass 1: coalesced background fills
                {
                    Cell cell = CellAt(r, c);
                    Color bg = EffectiveBg(cell);
                    if (bg == _theme.DefaultBackground) { c++; continue; }
                    int start = c;
                    while (c < cols)
                    {
                        Cell cc = CellAt(r, c);
                        if (cc.Width != 0 && EffectiveBg(cc) != bg) break;
                        c++;
                    }
                    brush.Color = C4(bg);
                    rt.FillRectangle(new Rect(ox + start * cw, y, (c - start) * cw, ch), brush);
                }
                int abs = hist - off + r;
                if (hasSel && abs >= sl0 && abs <= sl1)  // selection highlight (behind the text)
                {
                    bool block = selPane!.BlockSel;
                    int from = block ? Math.Min(sc0, sc1) : Math.Clamp(abs == sl0 ? sc0 : 0, 0, cols - 1);
                    int to = block ? Math.Max(sc0, sc1) : Math.Clamp(abs == sl1 ? sc1 : cols - 1, 0, cols - 1);
                    from = Math.Clamp(from, 0, cols - 1); to = Math.Clamp(to, 0, cols - 1);
                    if (to >= from)
                    {
                        brush.Color = new Color4(40 / 255f, 90 / 255f, 150 / 255f, 1f);
                        rt.FillRectangle(new Rect(ox + from * cw, y, (to - from + 1) * cw, ch), brush);
                    }
                }
                if (searchHere)  // search-match highlight (amber; current match brighter)
                {
                    for (int mi = 0; mi < _searchMatches.Count; mi++)
                    {
                        var m = _searchMatches[mi];
                        if (m.Line != abs) continue;
                        int from = Math.Clamp(m.Col0, 0, cols - 1), to = Math.Clamp(m.Col1, 0, cols - 1);
                        brush.Color = mi == _searchCur ? new Color4(235 / 255f, 175 / 255f, 45 / 255f, 1f)
                                                       : new Color4(150 / 255f, 110 / 255f, 25 / 255f, 1f);
                        rt.FillRectangle(new Rect(ox + from * cw, y, (to - from + 1) * cw, ch), brush);
                    }
                }
                if (_linkUrl is not null && selPane is not null && ReferenceEquals(selPane, _linkPane) && abs == _linkLine)
                {   // Ctrl+hovered link: accent underline under its column span
                    brush.Color = ChromeAccent;
                    float uy = y + ch - 1.5f;
                    rt.DrawLine(new System.Numerics.Vector2(ox + _linkC0 * cw, uy),
                                new System.Numerics.Vector2(ox + (_linkC1 + 1) * cw, uy), brush, 1.4f);
                }
                c = 0;
                while (c < cols)  // Pass 2: coalesced same-colour text runs
                {
                    Cell cell = CellAt(r, c);
                    if (cell.Width == 0 || cell.Rune == ' ' || cell.Rune == '\0') { c++; continue; }
                    Color runFg = EffectiveFg(cell);
                    // Builtin box-drawing / block elements: vector-draw for pixel-perfect borders
                    // (unhandled codepoints in the range fall back to the font glyph, drawn solo so
                    // the coalesced run never has to include them).
                    if (_config.BuiltinGlyphs && cell.Rune is (>= 0x2500 and <= 0x259F) or 0x2571 or 0x2572)
                    {
                        float bx = ox + c * cw;
                        if (!DrawBoxGlyph(rt, brush, cell.Rune, bx, y, cw, ch, C4(runFg)))
                        { brush.Color = C4(runFg); rt.DrawText(RuneStr(cell.Rune), fmt, new Rect(bx, y, bx + cw, y + ch), brush); }
                        c++; continue;
                    }
                    if (cell.Width == 2 || cell.Rune > 0xFFFF)
                    {
                        // Wide and astral glyphs draw individually: runs assume string index ==
                        // column, and an astral codepoint is two UTF-16 units in one column.
                        brush.Color = C4(runFg);
                        float wx = ox + c * cw;
                        rt.DrawText(RuneStr(cell.Rune), fmt, new Rect(wx, y, wx + cell.Width * cw, y + ch), brush);
                        c++;
                        continue;
                    }
                    int start = c;
                    _run.Clear();
                    int lastNonBlank = 0;
                    while (c < cols)
                    {
                        Cell cc = CellAt(r, c);
                        if (cc.Width == 2 || cc.Width == 0 || cc.Rune > 0xFFFF) break;
                        if (_config.BuiltinGlyphs && cc.Rune is (>= 0x2500 and <= 0x259F) or 0x2571 or 0x2572) break; // vector-drawn separately
                        bool blank = cc.Rune == ' ' || cc.Rune == '\0';
                        if (!blank && EffectiveFg(cc) != runFg) break;
                        _run.Append(blank ? ' ' : (char)cc.Rune);
                        c++;
                        if (!blank) lastNonBlank = _run.Length;
                    }
                    if (lastNonBlank > 0)
                    {
                        _run.Length = lastNonBlank;
                        brush.Color = C4(runFg);
                        float rx = ox + start * cw;
                        DrawRun(rt, brush, _run.ToString(), fmt, rx, y, _run.Length * cw, ch);
                    }
                }
            }

            if (drawImages)
                lock (session.SyncRoot) DrawImages(em, ox, oy, cw, ch);   // image placements read emulator state

            if (off > 0) // scrollback position indicator on the pane's right edge
            {
                float trackX = ox + cols * cw - 3f, trackH = rows * ch, total = hist + rows;
                float thumbH = MathF.Max(12f, trackH * rows / total);
                float thumbY = oy + (trackH - thumbH) * (hist - off) / MathF.Max(1, hist);
                brush.Color = WithA(ChromeText, 0.10f);
                rt.FillRectangle(new Rect(trackX, oy, 3f, trackH), brush);
                brush.Color = WithA(ChromeText, 0.35f);
                rt.FillRectangle(new Rect(trackX, thumbY, 3f, thumbH), brush);
            }
            if (marks.Length > 0)  // FTCS prompt pips (green ok / red fail / accent running)
            {
                float pipX = ox + cols * cw - 3f, pipTrackH = rows * ch, pipTotal = MathF.Max(1f, hist + rows);
                foreach (var m in marks)
                {
                    float py2 = oy + pipTrackH * m.PromptLine / pipTotal;
                    brush.Color = m.ExitCode is int ec2
                        ? (ec2 == 0 ? new Color4(0.24f, 0.78f, 0.35f, 0.9f) : new Color4(0.9f, 0.3f, 0.3f, 0.9f))
                        : WithA(ChromeAccent, 0.9f);
                    rt.FillRectangle(new Rect(pipX, py2, 3f, 2f), brush);
                }
            }

            if (cursorVisible && off == 0)
            {
                float cx = ox + cursorCol * cw, cy = oy + cursorRow * ch;
                brush.Color = C4(_theme.Cursor);
                if (!_config.CursorBlink || _cursorOn)
                {
                    switch (_config.CursorStyle)
                    {
                        case CursorStyle.Block: rt.FillRectangle(new Rect(cx, cy, cw, ch), brush); break;
                        case CursorStyle.Underline:
                            float uh = MathF.Max(1f, MathF.Round(ch * 0.12f));
                            rt.FillRectangle(new Rect(cx, cy + ch - uh, cw, uh), brush); break;
                        default:
                            float barW = MathF.Max(1f, MathF.Round(cw * 0.14f));
                            rt.FillRectangle(new Rect(cx, cy, barW, ch), brush); break;
                    }
                }
            }
        }
    }

    private void RenderBody()
    {
        var rt = _rt!;
        var brush = _brush!;
        rt.BeginDraw();
        rt.Clear(C4(_theme.DefaultBackground));

        // Dashboard grid overlay replaces the terminal content while it's up (agterm #202); the sidebar
        // and title bar still render below.
        if (_dashboardOpen) DrawDashboard(rt, brush);
        else DrawWindowContent(rt, brush);
        if (!_windowActive && _config.UnfocusedDim > 0)   // dim the content region when the window isn't focused
        {
            var (dx, dy, dw, dh) = ContentArea();
            brush.Color = new Color4(0f, 0f, 0f, Math.Clamp(_config.UnfocusedDim, 0, 90) / 100f);
            rt.FillRectangle(new Rect(dx, dy, dw, dh), brush);
        }
        DrawSidebar(rt, brush);
        DrawTitleBar(rt, brush);
        DrawSearchBar(rt, brush);
        DrawToast(rt, brush);
        DrawButtonTip(rt, brush);
        DrawHelp(rt, brush);   // topmost overlay
        DrawLeaderHint(rt, brush);
        DrawPalette(rt, brush);
        DrawSettingsPanel(rt, brush);
        DrawSwitcher(rt, brush);
    }

    /// <summary>The normal terminal content: the active session's pane grid, or a cover/quick/overlay panel.</summary>
    private void DrawWindowContent(ID2D1HwndRenderTarget rt, ID2D1SolidColorBrush brush)
    {
        // Quick terminal (kind 2) and a sized floating overlay (kind 3) both render as a centered
        // panel over the live main window — a "tool window" look. Scratch (1) / full overlay fill.
        bool floatingPanel = _cover is not null && ((_coverKind == 3 && _ovlOwner is { OverlaySizePercent: > 0 }) || _coverKind == 2);
        if (_cover is not null && !floatingPanel)
        {
            var (ox, oy, cw0, _) = ContentArea();
            var (fmt, cw, ch) = Metrics(_cover.FontSize);
            RenderTerminal(_cover.S, ox, oy, fmt, cw, ch, _cover.ScrollOffset, _cover);
            DrawCoverBadge(rt, brush, ox + cw0, oy);
            DrawOverlayFooter(rt, brush);
        }
        else
        {
            if (_active is not null) RenderPanes(rt, brush, _active);   // the main window shows behind
            if (floatingPanel)
            {
                var (cx0, cy0, cw0, ch0) = ContentArea();
                brush.Color = new Color4(0f, 0f, 0f, 0.45f);                 // dim scrim over the session
                rt.FillRectangle(new Rect(cx0, cy0, cw0, ch0), brush);
                var (fx, fy, fw, fh) = CoverRect();
                brush.Color = C4(_theme.DefaultBackground);                  // opaque panel
                rt.FillRectangle(new Rect(fx, fy, fw, fh), brush);
                var (fmt, cw, ch) = Metrics(_cover!.FontSize);
                RenderTerminal(_cover.S, fx, fy, fmt, cw, ch, _cover.ScrollOffset, _cover);
                brush.Color = ChromeAccent;                                  // 1px frame
                rt.DrawRectangle(new Rect(fx - 1f, fy - 1f, fw + 2f, fh + 2f), brush, 1f);
                DrawCoverBadge(rt, brush, fx + fw, fy);
                if (_coverKind == 3) DrawOverlayFooter(rt, brush);          // overlay-only footer
            }
        }
    }

    /// <summary>The Ctrl+Tab switcher HUD: a centered panel listing sessions in recency order with the
    /// current walk target highlighted. Painted only while a walk is in progress; never takes focus.</summary>
    private void DrawSwitcher(ID2D1HwndRenderTarget rt, ID2D1SolidColorBrush brush)
    {
        if (!_mruWalking || _mruSnapshot.Count < 2) return;
        int cw = ClientW(), ch = ClientH();
        brush.Color = PalScrim;
        rt.FillRectangle(new Rect(0, 0, cw, ch), brush);

        const float rowH = 36f, headH = 30f;
        int n = _mruSnapshot.Count;
        float pw = MathF.Min(460f, cw - 80f);
        float ph = headH + n * rowH + 10f;
        float px = (cw - pw) / 2f, py = MathF.Max(TitleBarH + 16f, (ch - ph) / 2f);
        var panel = new Rect(px, py, pw, ph);

        brush.Color = PalBg;
        rt.FillRoundedRectangle(new RoundedRectangle { Rect = panel, RadiusX = 10f, RadiusY = 10f }, brush);
        brush.Color = PalBorder;
        rt.DrawRoundedRectangle(new RoundedRectangle { Rect = panel, RadiusX = 10f, RadiusY = 10f }, brush, 1f);

        brush.Color = ChromeDim;
        rt.DrawText("Recent sessions — hold Ctrl, Tab to cycle", _uiSmall, new Rect(px + 16f, py + 8f, pw - 32f, 18f), brush);

        for (int i = 0; i < n; i++)
        {
            var s = _mruSnapshot[i];
            float ry = py + headH + i * rowH;
            if (i == _mruIdx) { brush.Color = PalSel; rt.FillRectangle(new Rect(px + 4f, ry, pw - 8f, rowH), brush); }
            brush.Color = StatusDot(AggStatus(s));
            rt.FillEllipse(new Ellipse(new System.Numerics.Vector2(px + 18f, ry + rowH / 2f), 4.5f, 4.5f), brush);
            brush.Color = i == _mruIdx ? SbActiveText : ChromeText;
            rt.DrawText(s.Name, _uiFont, new Rect(px + 32f, ry + (rowH - 20f) / 2f, pw - 150f, 20f), brush);
            brush.Color = ChromeDim;
            float wsw = MeasureText(s.Ws.Name, _uiSmall);
            rt.DrawText(s.Ws.Name, _uiSmall, new Rect(px + pw - 16f - wsw, ry + (rowH - 16f) / 2f, wsw + 2f, 16f), brush);
        }
    }

    /// <summary>Render a session's pane grid (terminals + split dividers + focused-pane accent).</summary>
    private void RenderPanes(ID2D1HwndRenderTarget rt, ID2D1SolidColorBrush brush, Ses ses)
    {
        var layout = PaneLayout(ses);
        foreach (var (pane, ox, oy, pw, ph) in layout)
        {
            var (fmt, cw, ch) = Metrics(pane.FontSize);
            DrawWatermark(ses, ox, oy, pw, ph);   // faint session background, behind the cells
            RenderTerminal(pane.S, ox, oy, fmt, cw, ch, pane.ScrollOffset, pane);
            // Dim non-active panes in a split so the focused one stands out.
            if (layout.Count > 1 && !ReferenceEquals(pane, ses.ActivePane) && _config.InactivePaneDim > 0)
            {
                brush.Color = WithA(new Color4(0f, 0f, 0f, 1f), System.Math.Clamp(_config.InactivePaneDim, 0, 100) / 100f * 0.9f);
                rt.FillRectangle(new Rect(ox, oy, pw, ph), brush);
            }
        }
        if (layout.Count > 1)
        {
            for (int i = 0; i < layout.Count - 1; i++)
            {
                float dx = layout[i].x + layout[i].w + DividerW / 2f;
                brush.Color = ChromeBorder;
                rt.FillRectangle(new Rect(dx - 0.5f, layout[i].y, 1f, layout[i].h), brush);
            }
            var ap = layout.First(l => ReferenceEquals(l.pane, ses.ActivePane));
            brush.Color = ChromeAccent;
            rt.FillRectangle(new Rect(ap.x, TitleBarH + 1f, ap.w, 2f), brush); // accent marks the focused pane
        }
    }

    /// <summary>Corner badge naming the current cover (scratch / quick / overlay); rightX/topY = cover top-right.</summary>
    /// <summary>The quick terminal's ✕ close box, anchored to the panel's top-right corner.</summary>
    private static (float x, float y, float w, float h) CoverCloseRect(float rightX, float topY) => (rightX - 26f, topY + 4f, 20f, 20f);

    private void DrawCoverBadge(ID2D1HwndRenderTarget rt, ID2D1SolidColorBrush brush, float rightX, float topY)
    {
        string badge = _coverKind switch { 1 => "scratch", 2 => "quick", 3 => "overlay", _ => "" };
        if (badge.Length == 0) return;
        if (_coverKind == 2)   // quick terminal: ✕ close box in the corner; the badge pill sits left of it
        {
            var (cx, cy, cw2, ch2) = CoverCloseRect(rightX, topY);
            brush.Color = WithA(SbHighlight, 0.92f);
            rt.FillRoundedRectangle(new RoundedRectangle { Rect = new Rect(cx, cy, cw2, ch2), RadiusX = 5f, RadiusY = 5f }, brush);
            brush.Color = ChromeText;
            rt.DrawText(GlyphClose, _iconFont, new Rect(cx + 4f, cy + 3f, cw2 - 4f, ch2 - 3f), brush);
            rightX = cx - 2f;
        }
        float bw = MeasureText(badge, _uiSmall) + 16f;
        brush.Color = WithA(SbHighlight, 0.92f);
        rt.FillRoundedRectangle(new RoundedRectangle { Rect = new Rect(rightX - bw - 6f, topY + 4f, bw, 20f), RadiusX = 5f, RadiusY = 5f }, brush);
        brush.Color = ChromeText;
        rt.DrawText(badge, _uiSmall, new Rect(rightX - bw + 2f, topY + 4f, bw - 8f, 20f), brush);
    }

    /// <summary>When a --wait overlay's program has exited, a footer banner in the cover inviting a key to close.</summary>
    private void DrawOverlayFooter(ID2D1HwndRenderTarget rt, ID2D1SolidColorBrush brush)
    {
        if (_coverKind != 3 || _ovlOwner is not { OverlayExited: true }) return;
        var (fx, fy, fw, fh) = CoverRect();
        string msg = $"  exited ({_overlayExitCode}) — press any key to close  ";
        float bh = 22f;
        brush.Color = WithA(ChromeAccent, 0.95f);
        rt.FillRectangle(new Rect(fx, fy + fh - bh, fw, bh), brush);
        brush.Color = new Color4(1f, 1f, 1f, 1f);
        rt.DrawText(msg, _uiSmall, new Rect(fx + 4f, fy + fh - bh, fw - 8f, bh), brush);
    }

    /// <summary>Find bar (top-right of the content region) shown while search is active.</summary>
    private void DrawSearchBar(ID2D1HwndRenderTarget rt, ID2D1SolidColorBrush brush)
    {
        if (!_searchActive) return;
        float barW = 340f, barH = 30f;
        float x = Math.Max(_sidebarW + 8f, ClientW() - barW - 12f), y = TitleBarH + 8f;
        var rr = new RoundedRectangle { Rect = new Rect(x, y, barW, barH), RadiusX = 6f, RadiusY = 6f };
        brush.Color = PalBg;
        rt.FillRoundedRectangle(rr, brush);
        brush.Color = ChromeAccent;
        rt.DrawRoundedRectangle(rr, brush, 1f);
        brush.Color = ChromeDim;
        rt.DrawText("", _iconFont, new Rect(x + 6f, y, 22f, barH), brush); // magnifier
        bool empty = _searchQuery.Length == 0;
        brush.Color = empty ? ChromeDim : ChromeText;
        rt.DrawText(empty ? "Find" : _searchQuery + "▏", _uiFont, new Rect(x + 30f, y, barW - 116f, barH), brush);
        string status = SearchStatus();
        if (status.Length > 0)
        {
            brush.Color = ChromeDim;
            rt.DrawText(status, _uiSmall, new Rect(x + barW - 82f, y, 76f, barH), brush);
        }
    }
}
