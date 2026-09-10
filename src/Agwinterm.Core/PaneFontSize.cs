namespace Agwinterm.Core;

/// <summary>Default-size inheritance is explicit: zooming back to the same number is still a zoom;
/// only reset resumes inheritance. Legacy state without the flag infers it from the saved size.</summary>
public static class PaneFontSize
{
    public static (float Size, bool Zoomed) Restore(float saved, float currentDefault, bool? zoomed)
    {
        bool explicitSize = zoomed ?? (saved > 0 && saved != currentDefault);
        return (explicitSize && saved > 0 ? saved : currentDefault, explicitSize && saved > 0);
    }

    public static (float Size, bool Zoomed) Zoom(float size, float currentDefault, int delta)
        => delta == 0 ? (currentDefault, false) : (Math.Clamp(size + delta, 6f, 48f), true);
}
