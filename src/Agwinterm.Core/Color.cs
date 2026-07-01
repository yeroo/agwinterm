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
