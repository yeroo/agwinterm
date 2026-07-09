using System;
using System.Collections.Generic;
using System.Linq;
using Vortice.Direct2D1;
using Vortice.Mathematics;
using static Agwinterm.Win32.Win32;

namespace Agwinterm.Win32;

/// <summary>
/// F1 Help overlay: a themed scrollable card listing how agwinterm works, the EFFECTIVE key
/// bindings (keymap.conf included), and an accessibility guide. When a screen reader is attached
/// the guide is spoken on open and the whole help text is exposed as a modal UIA document, so a
/// low-vision user gets an audio orientation of what they can do and how.
/// F1 opens it from the shell prompt; full-screen TUIs (alt screen: Far, vim) keep their own F1.
/// </summary>
internal partial class Program
{
    private bool _helpOpen;
    private float _helpScroll;
    private string[] _helpLines = Array.Empty<string>();
    private Rect _helpCard;

    private void OpenHelp()
    {
        _helpLines = BuildHelpLines();
        _helpOpen = true; _helpScroll = 0;
        if (Uia.ClientsListening) Uia.Announce(NarratorGuide());
        RequestRedraw();
    }

    private void CloseHelp()
    {
        _helpOpen = false;
        Uia.Announce("Help closed");
        RequestRedraw();
    }

    /// <summary>Spoken orientation for screen-reader users (concise; the full text is also readable
    /// as the modal help document via Caps+arrows).</summary>
    private static string NarratorGuide() =>
        "Help. agwinterm is a terminal with a session sidebar. " +
        "The terminal reads and navigates like a document: use Narrator's line commands to read output; new output is spoken automatically. " +
        "Press F6 to move focus between the terminal and the session list, then Up and Down to pick a session and Enter to open it. " +
        "Press Control Shift T for a new session, Control Backtick for the quick terminal, Control J for the scratch terminal. " +
        "The Settings dialog opens from the gear button and is fully keyboard navigable: Tab moves between controls, Space changes them, Page Up and Page Down switch tabs, Escape closes. " +
        "In this help screen, use Down and Up to scroll, Escape to close.";

    private string[] BuildHelpLines()
    {
        var lines = new List<string>
        {
            "GETTING STARTED",
            "agwinterm is an agent-first terminal: sessions live in the left sidebar,",
            "each with a status dot an agent can set via the control API (agwintermctl).",
            "",
            "FOCUS & NAVIGATION",
            "F6            move focus between terminal and sidebar (arrows + Enter there)",
            "F1            this help (at the shell prompt; full-screen apps keep their F1)",
            "Esc           close overlays (help, settings, palettes, search)",
            "",
            "KEY BINDINGS (effective — keymap.conf applied)",
        };
        foreach (var kv in _keymap.OrderBy(k => FriendlyAction(k.Value), StringComparer.OrdinalIgnoreCase))
            lines.Add($"{PrettyChord(kv.Key),-20}{FriendlyAction(kv.Value)}");
        lines.AddRange(new[]
        {
            "",
            "ACCESSIBILITY",
            "The terminal is a UIA document: screen readers read it line by line and",
            "track the caret; new output is announced automatically after it settles.",
            "Settings is fully keyboard navigable (Tab / Space / PageUp / PageDown) and",
            "buttons speak on hover and show tooltips. Sessions, buttons, and settings",
            "controls are all scannable elements (Narrator: Caps Lock + arrows).",
            "",
            "MORE",
            "Settings: gear button or Ctrl+Shift+P → Settings.  Control API: agwintermctl --help.",
        });
        return lines.ToArray();
    }

    private static string PrettyChord(string chord) =>
        string.Join("+", chord.Split('+').Select(p => p.Length == 1 ? p.ToUpperInvariant()
            : char.ToUpperInvariant(p[0]) + p[1..]));

    private static string FriendlyAction(string a) =>
        a.Replace('_', ' ') is { Length: > 0 } s ? char.ToUpperInvariant(s[0]) + s[1..] : a;

    /// <summary>The whole help text (the UIA modal document's name while help is open).</summary>
    private string HelpText() => string.Join("\n", _helpLines);

    private void DrawHelp(ID2D1HwndRenderTarget rt, ID2D1SolidColorBrush brush)
    {
        if (!_helpOpen) return;
        int cw = ClientW(), ch = ClientH();
        brush.Color = new Color4(0f, 0f, 0f, 0.45f);           // dim the app behind
        rt.FillRectangle(new Rect(0, 0, cw, ch), brush);

        float cardW = MathF.Min(640f, cw - 60f), cardH = MathF.Min(680f, ch - 80f);
        float cx = (cw - cardW) / 2f, cy = MathF.Max(TitleBarH + 16f, (ch - cardH) / 2f);
        _helpCard = new Rect(cx, cy, cardW, cardH);
        brush.Color = PalBg;
        rt.FillRoundedRectangle(new RoundedRectangle { Rect = _helpCard, RadiusX = 10f, RadiusY = 10f }, brush);
        brush.Color = PalBorder;
        rt.DrawRoundedRectangle(new RoundedRectangle { Rect = _helpCard, RadiusX = 10f, RadiusY = 10f }, brush, 1f);

        brush.Color = ChromeText;
        rt.DrawText("Help", _uiFont, new Rect(cx + 20f, cy + 12f, 200f, 24f), brush);
        brush.Color = ChromeDim;
        rt.DrawText("Esc to close · ↑↓ scroll", _uiSmall, new Rect(cx + cardW - 190f, cy + 15f, 180f, 20f), brush);

        float top = cy + 44f, bottom = cy + cardH - 14f;
        rt.PushAxisAlignedClip(new Rect(cx, top, cardW, bottom - top), AntialiasMode.Aliased);
        float y = top - _helpScroll;
        foreach (var line in _helpLines)
        {
            if (y > bottom) break;
            if (y + 18f > top)
            {
                bool section = line.Length > 0 && line == line.ToUpperInvariant() && !line.Contains("  ") && line[0] != ' ' && !char.IsDigit(line[0]) && line.Length < 60 && line.All(c => !char.IsLower(c));
                brush.Color = section ? ChromeAccent : ChromeText;
                rt.DrawText(line, section ? _uiSmall : _uiSmall, new Rect(cx + 20f, y, cardW - 40f, 18f), brush);
            }
            y += 18f;
        }
        _helpContentH = _helpLines.Length * 18f;
        rt.PopAxisAlignedClip();
    }

    private float _helpContentH;

    /// <summary>Key handling while help is open; swallows everything (modal).</summary>
    private bool HelpKey(int vk)
    {
        float view = _helpCard.Bottom - _helpCard.Top - 58f;
        float max = MathF.Max(0f, _helpContentH - view);
        switch (vk)
        {
            case VK_ESCAPE: case 0x70 /* F1 */: CloseHelp(); return true;
            case VK_DOWN: _helpScroll = Math.Clamp(_helpScroll + 36f, 0f, max); RequestRedraw(); return true;
            case VK_UP: _helpScroll = Math.Clamp(_helpScroll - 36f, 0f, max); RequestRedraw(); return true;
            case VK_NEXT: _helpScroll = Math.Clamp(_helpScroll + view, 0f, max); RequestRedraw(); return true;
            case VK_PRIOR: _helpScroll = Math.Clamp(_helpScroll - view, 0f, max); RequestRedraw(); return true;
            case VK_HOME: _helpScroll = 0f; RequestRedraw(); return true;
            case VK_END: _helpScroll = max; RequestRedraw(); return true;
        }
        return true;
    }
}
