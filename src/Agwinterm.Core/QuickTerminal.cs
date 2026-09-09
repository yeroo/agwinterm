namespace Agwinterm.Core;

/// <summary>Platform-neutral quick-panel policy; modifiers use the Win32 hotkey bit values.</summary>
public readonly record struct QuickHotkey(uint Modifiers, uint Key)
{
    public static bool TryParse(string text, out QuickHotkey chord)
    {
        chord = default;
        if (string.IsNullOrWhiteSpace(text)) return true; // disabled
        var parts = text.Trim().ToLowerInvariant().Split('+', StringSplitOptions.TrimEntries);
        uint mods = 0;
        for (int i = 0; i < parts.Length - 1; i++)
        {
            uint bit = parts[i] switch { "alt" => 1u, "ctrl" => 2u, "shift" => 4u, _ => 0u };
            if (bit == 0 || (mods & bit) != 0) return false;
            mods |= bit;
        }
        if ((mods & 3) == 0) return false;
        string key = parts[^1];
        uint vk = key.Length == 1 && (key[0] is >= 'a' and <= 'z' or >= '0' and <= '9')
            ? char.ToUpperInvariant(key[0])
            : key == "backtick" ? 0xC0u
            : key.StartsWith('f') && int.TryParse(key.AsSpan(1), out int f) && f is >= 1 and <= 11
                ? (uint)(0x6F + f) : 0;
        if (vk == 0) return false;
        chord = new(mods, vk);
        return true;
    }
}

public static class QuickTerminalGeometry
{
    public const int DefaultPercent = 70;
    public static (int X, int Y, int Width, int Height) Frame(int x, int y, int width, int height, int percent)
    {
        int p = Math.Clamp(percent, 40, 90);
        int w = Math.Max(1, (int)((long)Math.Max(1, width) * p / 100));
        int h = Math.Max(1, (int)((long)Math.Max(1, height) * p / 100));
        return (x + (width - w) / 2, y + (height - h) / 2, w, h);
    }
}
