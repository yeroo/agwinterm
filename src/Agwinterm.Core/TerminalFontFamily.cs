namespace Agwinterm.Core;

/// <summary>Resolve missing configured families before DirectWrite can silently substitute a proportional face.</summary>
public static class TerminalFontFamily
{
    public static string Resolve(string configured, Func<string, bool> installed)
    {
        if (!string.IsNullOrWhiteSpace(configured) && installed(configured)) return configured;
        foreach (string fallback in new[] { "Cascadia Mono", "Consolas", "Lucida Console", "Courier New" })
            if (installed(fallback)) return fallback;
        throw new InvalidOperationException("No supported monospace fallback font is installed; install Consolas or Cascadia Mono.");
    }
}
