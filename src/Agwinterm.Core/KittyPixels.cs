namespace Agwinterm.Core;

/// <summary>
/// Converts a transmitted image's raw pixel payload into the premultiplied-BGRA buffer the
/// Direct2D upload path wants. Lives in Core rather than in the renderer so the format-selection
/// logic is testable without a device: the renderer's only job is to hand the result to D2D.
/// PNG payloads are not decoded here (that needs an imaging stack); the renderer keeps that arm.
/// </summary>
public static class KittyPixels
{
    /// <summary>
    /// Convert <paramref name="img"/>'s raw pixels to premultiplied BGRA, width*height*4 bytes.
    /// <see cref="KittyFormat.Bgra"/> takes the no-swizzle route - the channels are already in
    /// target order, so only the alpha multiply remains - while RGB/RGBA swap red and blue.
    /// Short payloads stop early and leave the tail zeroed rather than reading out of bounds:
    /// the bytes can come from another process through <c>image.frameshm</c>.
    /// </summary>
    public static byte[] ToPremultipliedBgra(KittyImage img)
    {
        var d = img.Data;
        int w = Math.Max(0, img.Width), h = Math.Max(0, img.Height);
        var outb = new byte[w * h * 4];
        if (img.Format == KittyFormat.Bgra)
        {
            // Already B,G,R,A: premultiply in place, no channel swap. This is the whole reason
            // the format exists - at 1920x1080x30fps the swizzle is a per-frame full-buffer pass.
            for (int i = 0, j = 0; i + 3 < d.Length && j + 3 < outb.Length; i += 4, j += 4)
            {
                byte a = d[i + 3];
                outb[j] = (byte)(d[i] * a / 255); outb[j + 1] = (byte)(d[i + 1] * a / 255);
                outb[j + 2] = (byte)(d[i + 2] * a / 255); outb[j + 3] = a;
            }
        }
        else if (img.Format == KittyFormat.Rgba)
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
}
