namespace Agwinterm.Core;

/// <summary>Relative navigation in the visible workspace order; expansion does not affect order.</summary>
public static class WorkspaceNavigation
{
    public static bool TryDirection(string? direction, out int step)
    {
        step = direction switch { "next" => 1, "prev" or "previous" => -1, _ => 0 };
        return step != 0;
    }

    public static int Destination(int count, int current, int step)
    {
        if (count < 2 || step is not (-1 or 1)) return -1;
        if (current < 0 || current >= count) return step > 0 ? 0 : count - 1;
        return (current + step + count) % count;
    }
}

/// <summary>A conservative Windows shell-name hint, never proof of an interactive prompt.</summary>
public static class ForegroundShellNames
{
    public static string? Recognize(string? executable, bool hasChildren, bool isLive)
    {
        if (!isLive || hasChildren || string.IsNullOrEmpty(executable)) return null;
        string name = executable.ToLowerInvariant();
        if (name.EndsWith(".exe", StringComparison.Ordinal)) name = name[..^4];
        return name is "cmd" or "powershell" or "pwsh" or "bash" or "sh" or "zsh" or "fish" or "nu"
            ? name : null;
    }
}
