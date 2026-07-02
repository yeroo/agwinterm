using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

// agwinterm app icon generator — three variants.
//  v1 "signal" : chevron + block cursor + green agent-status dot (identity)
//  v2 "clean"  : bold centered ">_" prompt, cyan, no dot (pure terminal)
//  v3 "gradient": chevron with cyan->blue gradient + cursor + small dot
static class Program
{
    static void Main(string[] args)
    {
        string outDir = args.Length > 0 ? args[0] : ".";
        Directory.CreateDirectory(outDir);
        int[] sizes = { 16, 20, 24, 32, 40, 48, 64, 128, 256 };
        foreach (string v in new[] { "signal", "clean", "gradient" })
        {
            var pngs = new List<(int size, byte[] data)>();
            foreach (int s in sizes)
            {
                using var bmp = Render(s, v);
                using var ms = new MemoryStream();
                bmp.Save(ms, ImageFormat.Png);
                pngs.Add((s, ms.ToArray()));
                if (s == 256) File.WriteAllBytes(Path.Combine(outDir, $"preview-{v}.png"), ms.ToArray());
            }
            WriteIco(Path.Combine(outDir, $"agwinterm-{v}.ico"), pngs);
        }
        Console.WriteLine("done");
    }

    static Bitmap Render(int S, string variant)
    {
        var bmp = new Bitmap(S, S, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.Clear(Color.Transparent);

        float f = S;
        float pad = f * 0.055f;
        var tile = new RectangleF(pad, pad, f - 2 * pad, f - 2 * pad);
        float radius = tile.Width * 0.235f;

        using (var path = Rounded(tile, radius))
        {
            using var grad = new LinearGradientBrush(
                new PointF(tile.Left, tile.Top), new PointF(tile.Left, tile.Bottom),
                Color.FromArgb(255, 0x25, 0x31, 0x40), Color.FromArgb(255, 0x10, 0x18, 0x22));
            g.FillPath(grad, path);

            var sheen = new RectangleF(tile.Left, tile.Top, tile.Width, tile.Height * 0.5f);
            using (var sg = new LinearGradientBrush(
                new PointF(0, tile.Top), new PointF(0, tile.Top + tile.Height * 0.5f),
                Color.FromArgb(38, 255, 255, 255), Color.FromArgb(0, 255, 255, 255)))
            { var clip = g.Clip; g.SetClip(path); g.FillRectangle(sg, sheen); g.Clip = clip; }

            if (S >= 24)
                using (var pen = new Pen(Color.FromArgb(60, 120, 170, 210), Math.Max(1f, f * 0.006f)))
                    g.DrawPath(pen, path);
        }

        Color cyan = Color.FromArgb(255, 0x38, 0xD7, 0xF0);
        Color cyanLt = Color.FromArgb(255, 0x8A, 0xE2, 0xF2);
        Color green = Color.FromArgb(255, 0x3D, 0xDC, 0x84);

        if (variant == "clean")
        {
            // bold centered ">_" : chevron + underscore bar
            float top = f * 0.30f, mid = f * 0.50f, bot = f * 0.70f;
            float cx = f * 0.285f, reach = f * 0.165f;
            float stroke = Math.Max(1.6f, f * 0.085f);
            using (var pen = new Pen(cyan, stroke)
            { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
                g.DrawLines(pen, new[] { new PointF(cx, top), new PointF(cx + reach, mid), new PointF(cx, bot) });
            // underscore
            float uw = f * 0.235f, uh = Math.Max(1.6f, f * 0.075f);
            float ux = f * 0.515f, uy = bot - uh * 0.2f;
            using (var up = Rounded(new RectangleF(ux, uy, uw, uh), uh / 2f))
            using (var ub = new SolidBrush(cyanLt)) g.FillPath(ub, up);
        }
        else
        {
            // signal & gradient share chevron + block cursor
            float top = f * 0.335f, mid = f * 0.50f, bot = f * 0.665f;
            float cx = f * 0.315f, reach = f * 0.135f;
            float stroke = Math.Max(1.4f, f * 0.072f);
            Brush strokeBrush = variant == "gradient"
                ? new LinearGradientBrush(new PointF(cx, top), new PointF(cx + reach, bot),
                    Color.FromArgb(255, 0x4A, 0xE0, 0xF5), Color.FromArgb(255, 0x3B, 0x82, 0xF6))
                : new SolidBrush(cyan);
            using (strokeBrush)
            using (var pen = new Pen(strokeBrush, stroke)
            { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
                g.DrawLines(pen, new[] { new PointF(cx, top), new PointF(cx + reach, mid), new PointF(cx, bot) });

            float curW = f * 0.175f, curH = f * 0.115f, curX = f * 0.545f, curY = bot - curH;
            using (var cp = Rounded(new RectangleF(curX, curY, curW, curH), Math.Min(curW, curH) * 0.32f))
            using (var cb = new SolidBrush(cyanLt)) g.FillPath(cb, cp);

            if (S >= 24)
            {
                float dotR = f * (variant == "gradient" ? 0.045f : 0.052f);
                float dcx = f * 0.70f, dcy = f * 0.335f;
                using (var gb = new SolidBrush(Color.FromArgb(70, green.R, green.G, green.B)))
                    g.FillEllipse(gb, dcx - dotR * 2.1f, dcy - dotR * 2.1f, dotR * 4.2f, dotR * 4.2f);
                using (var db = new SolidBrush(green))
                    g.FillEllipse(db, dcx - dotR, dcy - dotR, dotR * 2, dotR * 2);
            }
        }
        return bmp;
    }

    static GraphicsPath Rounded(RectangleF r, float rad)
    {
        rad = Math.Min(rad, Math.Min(r.Width, r.Height) / 2f);
        float d = rad * 2f;
        var p = new GraphicsPath();
        p.AddArc(r.Left, r.Top, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Top, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    static void WriteIco(string path, List<(int size, byte[] data)> imgs)
    {
        using var fs = new FileStream(path, FileMode.Create);
        using var w = new BinaryWriter(fs);
        w.Write((short)0); w.Write((short)1); w.Write((short)imgs.Count);
        int offset = 6 + imgs.Count * 16;
        foreach (var (size, data) in imgs)
        {
            w.Write((byte)(size >= 256 ? 0 : size)); w.Write((byte)(size >= 256 ? 0 : size));
            w.Write((byte)0); w.Write((byte)0); w.Write((short)1); w.Write((short)32);
            w.Write(data.Length); w.Write(offset); offset += data.Length;
        }
        foreach (var (_, data) in imgs) w.Write(data);
    }
}
