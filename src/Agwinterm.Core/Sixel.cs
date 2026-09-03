namespace Agwinterm.Core;

/// <summary>
/// Decodes a DCS sixel payload (the bytes between <c>ESC P … q</c> and <c>ST</c>) into an RGBA
/// bitmap. Handles the color introducer (<c>#</c>, RGB/HLS define + select), raster attributes
/// (<c>"</c>), repeat (<c>!</c>), carriage return (<c>$</c>), line feed (<c>-</c>), and the sixel
/// data bytes (0x3F–0x7E, six vertical pixels each). Background stays transparent.
/// </summary>
public static class Sixel
{
    private const int MaxDimension = 16_384;
    private const int MaxCanvasBytes = 64 * 1024 * 1024;
    private const long MaxCanvasPixels = MaxCanvasBytes / 4;
    private const long MaxDecodeWork = MaxCanvasPixels;

    /// <summary>Result of decoding: RGBA pixels (width*height*4) or null if not a sixel payload.</summary>
    public static (int Width, int Height, byte[] Rgba)? Decode(byte[] dcs)
    {
        // The DCS payload is "P1;P2;P3 q <sixel-data>". Skip params up to and including 'q'.
        int i = 0;
        while (i < dcs.Length && dcs[i] != (byte)'q') i++;
        if (i >= dcs.Length) return null;   // no sixel introducer
        i++;

        // The VT-241 default 16-color palette (indices 0..15); apps usually redefine what they use.
        var palette = new (byte R, byte G, byte B)[256];
        int[] defaults = { 0x000000, 0x3333CC, 0xCC2121, 0x33CC33, 0xCC33CC, 0x33CCCC, 0xCCCC33, 0x878787,
                           0x424242, 0x545499, 0x994242, 0x549954, 0x995499, 0x549999, 0x999954, 0xCCCCCC };
        for (int p = 0; p < 256; p++)
        {
            int d = p < defaults.Length ? defaults[p] : 0xFFFFFF;
            palette[p] = ((byte)(d >> 16), (byte)(d >> 8), (byte)d);
        }

        // Grow-as-needed RGBA canvas (sixel doesn't have to declare its size up front).
        int cap = 64;
        int width = 0, height = 0;              // used extent
        byte[] rgba = new byte[cap * cap * 4];
        int canvasW = cap, canvasH = cap;
        int curColor = 0, x = 0, y = 0;         // y = top of the current 6-pixel band

        bool EnsureSize(int needW, int needH)
        {
            // Bound both dimensions and their product. A per-dimension clamp alone permits a tiny
            // raster declaration to allocate a 16384x16384 (1 GiB) canvas.
            if (needW <= 0 || needH <= 0 || needW > MaxDimension || needH > MaxDimension)
                return false;
            if (needW <= canvasW && needH <= canvasH) return true;
            int nw = canvasW, nh = canvasH;
            while (nw < needW) nw *= 2;
            while (nh < needH) nh *= 2;
            long bytes = (long)nw * nh * 4;
            if (bytes > MaxCanvasBytes) return false;
            var ng = new byte[(int)bytes];
            for (int row = 0; row < canvasH; row++)
                System.Array.Copy(rgba, row * canvasW * 4, ng, row * nw * 4, canvasW * 4);
            rgba = ng; canvasW = nw; canvasH = nh;
            return true;
        }

        int ReadInt(ref int j)
        {
            int v = 0; bool any = false;
            while (j < dcs.Length && dcs[j] >= (byte)'0' && dcs[j] <= (byte)'9') { v = v * 10 + (dcs[j] - '0'); j++; any = true; }
            return any ? v : -1;
        }

        bool rejected = false;
        long decodeWork = 0;
        try
        {
            while (i < dcs.Length && !rejected)
            {
                byte b = dcs[i];
                if (b == (byte)'#')   // color: #Pc  (select)  or  #Pc;Pu;Px;Py;Pz  (define)
                {
                    i++;
                    int idx = ReadInt(ref i);
                    if (idx < 0 || idx > 255) { continue; }
                    if (i < dcs.Length && dcs[i] == (byte)';')
                    {
                        i++; int space = ReadInt(ref i);
                        Skip(ref i, ';'); int a = ReadInt(ref i);
                        Skip(ref i, ';'); int bb = ReadInt(ref i);
                        Skip(ref i, ';'); int cc = ReadInt(ref i);
                        if (space == 2)   // RGB, components 0..100
                            palette[idx] = ((byte)(a * 255 / 100), (byte)(bb * 255 / 100), (byte)(cc * 255 / 100));
                        else if (space == 1)   // HLS (hue 0..360, light/sat 0..100)
                            palette[idx] = HlsToRgb(a, bb, cc);
                    }
                    curColor = idx;
                }
                else if (b == (byte)'"')   // raster attributes: "Pan;Pad;Ph;Pv  (aspect + declared size)
                {
                    i++; ReadInt(ref i); Skip(ref i, ';'); ReadInt(ref i);
                    Skip(ref i, ';'); int ph = ReadInt(ref i); Skip(ref i, ';'); int pv = ReadInt(ref i);
                    if (ph > 0 && pv > 0 && !EnsureSize(ph, pv)) rejected = true;
                }
                else if (b == (byte)'!')   // repeat: !Pn <sixel>
                {
                    i++; int n = ReadInt(ref i);
                    if (i < dcs.Length && dcs[i] >= 0x3F && dcs[i] <= 0x7E)
                    {
                        if (!PutSixel(dcs[i] - 0x3F, System.Math.Max(1, n))) rejected = true;
                        i++;
                    }
                }
                else if (b == (byte)'$') { x = 0; i++; }              // CR: back to left, same band
                else if (b == (byte)'-') { x = 0; y += 6; i++; }      // LF: next 6-pixel band
                else if (b >= 0x3F && b <= 0x7E)
                {
                    if (!PutSixel(b - 0x3F, 1)) rejected = true;
                    i++;
                }
                else i++;   // ignore anything else (whitespace, stray bytes)
            }
        }
        catch (OutOfMemoryException) { rejected = true; }
        catch { /* be forgiving: return whatever decoded so far */ }

        if (rejected || width <= 0 || height <= 0) return null;

        // Crop the canvas to the used extent.
        byte[] outp;
        try { outp = new byte[checked(width * height * 4)]; }
        catch (OutOfMemoryException) { return null; }
        catch (OverflowException) { return null; }
        for (int row = 0; row < height; row++)
            System.Array.Copy(rgba, row * canvasW * 4, outp, row * width * 4, width * 4);
        return (width, height, outp);

        void Skip(ref int j, char ch) { if (j < dcs.Length && dcs[j] == (byte)ch) j++; }

        bool PutSixel(int bits, int repeat)
        {
            long work = (long)repeat * 6;
            if (work > MaxDecodeWork - decodeWork) return false;
            decodeWork += work;

            long needW = (long)x + repeat;
            long needH = (long)y + 6;
            if (needW > MaxDimension || needH > MaxDimension) return false;

            // A zero-bit sixel advances the position but paints nothing. Do that in O(1), which
            // keeps a tiny !2147483647? payload from tying up the output pump for billions of loops.
            if (bits == 0)
            {
                x = (int)needW;
                return true;
            }

            if (!EnsureSize((int)needW, (int)needH)) return false;
            var (r, g, bl) = palette[curColor];
            for (int rep = 0; rep < repeat; rep++, x++)
                for (int row = 0; row < 6; row++)
                    if ((bits & (1 << row)) != 0)
                    {
                        int px = x, py = y + row;
                        if (px >= canvasW || py >= canvasH) throw new InvalidOperationException();
                        int o = (py * canvasW + px) * 4;
                        rgba[o] = r; rgba[o + 1] = g; rgba[o + 2] = bl; rgba[o + 3] = 255;
                        if (px + 1 > width) width = px + 1;
                        if (py + 1 > height) height = py + 1;
                    }
            return true;
        }
    }

    private static (byte, byte, byte) HlsToRgb(int h, int l, int s)
    {
        double L = l / 100.0, S = s / 100.0, H = ((h % 360) + 360) % 360 / 360.0;
        if (S == 0) { byte v = (byte)(L * 255); return (v, v, v); }
        double q = L < 0.5 ? L * (1 + S) : L + S - L * S, p = 2 * L - q;
        double R = Hue(p, q, H + 1.0 / 3), G = Hue(p, q, H), B = Hue(p, q, H - 1.0 / 3);
        return ((byte)(R * 255), (byte)(G * 255), (byte)(B * 255));
        static double Hue(double p, double q, double t)
        {
            if (t < 0) t += 1; if (t > 1) t -= 1;
            if (t < 1.0 / 6) return p + (q - p) * 6 * t;
            if (t < 1.0 / 2) return q;
            if (t < 2.0 / 3) return p + (q - p) * (2.0 / 3 - t) * 6;
            return p;
        }
    }
}
