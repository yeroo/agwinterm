using System.Globalization;
using System.Numerics;
using Agwinterm.Pty;
using Vortice.Direct2D1;
using Vortice.DirectWrite;
using Vortice.Mathematics;
using static Agwinterm.Win32.Win32;

namespace Agwinterm.Win32;

internal partial class Program
{
    private const int HudTimer = 14;

    public string SessionHud(string? target, string action, HudSpec? spec) => InvokeOnUiQueued(() =>
    {
        // Session identity must be explicit if a split/auxiliary pane was named. A HUD is not
        // a pane overlay and has no PTY id of its own. Resolve/check/write in this same UI hop.
        var ses = FindSesForTarget(target);
        if (ses is null) return ISessionHost.RefusePrefix + "hud: no session matches that target";
        if (string.IsNullOrEmpty(target) || target == "active")
        {
            if (_cover is not null) return ISessionHost.RefusePrefix + "hud: active surface is a cover; pass the session id";
        }
        else if (FindControlPane(target) is { } resolved &&
                 (!ses.Panes.Contains(resolved.pane) ||
                  (ses.Panes.Count > 1 && target != ses.Id && !ses.Id.StartsWith(target, StringComparison.Ordinal) &&
                   ses.Panes.Any(p => p.Id == target || p.Id.StartsWith(target, StringComparison.Ordinal)))))
            return ISessionHost.RefusePrefix + "hud: target names a pane or cover; pass the owning session id";

        if (action == "close") ClearHud(ses); // never touches a program overlay
        else
        {
            if (ses.Overlay.Term is not null) return ISessionHost.RefusePrefix + "hud: a program overlay occupies the slot";
            if (action == "update" && ses.Hud is null) return ISessionHost.RefusePrefix + "hud: no HUD to update";
            if (spec is null) return ISessionHost.RefusePrefix + "hud: missing message specification";
            lock (_workspaces) ses.Hud = action == "update" ? spec with { BackgroundColor = ses.Hud!.BackgroundColor } : spec;
            RefreshHudTimer(); RequestRedraw();
        }
        return SessionHuds.Reply(ses.Id, ses.Hud);
    });

    private void ClearHud(Ses ses)
    {
        if (ses.Hud is null) return;
        lock (_workspaces) ses.Hud = null;
        RefreshHudTimer(); RequestRedraw();
    }

    private void RefreshHudTimer()
    {
        bool spinning;
        lock (_workspaces) spinning = _workspaces.Any(w => w.Sessions.Any(s => s.Hud is { Spinner: not "none" }));
        if (spinning) SetTimer(_hwnd, (IntPtr)HudTimer, 80, IntPtr.Zero);
        else KillTimer(_hwnd, (IntPtr)HudTimer);
    }

    private void HudTick()
    {
        if (!_dashboardOpen && _cover is null && _active?.Hud is { Spinner: not "none" }) RequestRedraw();
    }

    private void DrawHud(ID2D1HwndRenderTarget rt, ID2D1SolidColorBrush brush, HudSpec hud)
    {
        var (x, y, w, h) = ContentArea();
        if (w < 2 || h < 2) return;
        float spinnerWidth = hud.Spinner == "none" ? 0 : 24;
        float maxWidth = Math.Max(1, w * .8f - 24 - spinnerWidth);
        using var measure = _dwrite.CreateTextLayout(hud.Message, _uiFont, maxWidth, 10000);
        measure.WordWrapping = WordWrapping.Wrap;
        float naturalWidth = measure.Metrics.Width;
        if (hud.Detail is { } detail)
        {
            using var detailMeasure = _dwrite.CreateTextLayout(detail, _uiSmall, maxWidth, 10000);
            detailMeasure.WordWrapping = WordWrapping.Wrap;
            naturalWidth = Math.Max(naturalWidth, detailMeasure.Metrics.Width);
        }
        var initial = SessionHuds.Place(x, y, w, h, naturalWidth + 24 + spinnerWidth, 0, hud);
        float textWidth = Math.Max(1, initial.Width - 24 - spinnerWidth);
        using var title = _dwrite.CreateTextLayout(hud.Message, _uiFont, textWidth, 10000);
        title.WordWrapping = WordWrapping.Wrap; title.ParagraphAlignment = ParagraphAlignment.Near;
        using var subtitle = _dwrite.CreateTextLayout(hud.Detail ?? "", _uiSmall, textWidth, 10000);
        subtitle.WordWrapping = WordWrapping.Wrap; subtitle.ParagraphAlignment = ParagraphAlignment.Near;
        float titleHeight = title.Metrics.Height;
        float detailHeight = hud.Detail is null ? 0 : subtitle.Metrics.Height + 6;
        var box = SessionHuds.Place(x, y, w, h, naturalWidth + 24 + spinnerWidth, titleHeight + detailHeight + 24, hud);
        var rect = new Rect(box.X, box.Y, box.Width, box.Height);
        rt.PushAxisAlignedClip(rect, AntialiasMode.PerPrimitive);
        try
        {
            static Color4 ColorOf(string? hex, Color4 fallback)
            {
                if (hex is null) return fallback;
                uint n = uint.Parse(hex.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                return new Color4(((n >> 16) & 255) / 255f, ((n >> 8) & 255) / 255f, (n & 255) / 255f, 1);
            }
            brush.Color = ColorOf(hud.BackgroundColor, C4(_theme.DefaultBackground));
            rt.FillRectangle(rect, brush);
            brush.Color = ChromeAccent; rt.DrawRectangle(rect, brush, 1);
            var fg = ColorOf(hud.TextColor, C4(_theme.DefaultForeground));
            brush.Color = fg;
            if (spinnerWidth > 0)
                rt.DrawText(SessionHuds.Frame(hud.Spinner, Environment.TickCount64), _uiFont,
                    new Rect(box.X + 10, box.Y + 12, spinnerWidth, titleHeight), brush, DrawTextOptions.Clip);
            rt.DrawTextLayout(new Vector2(box.X + 12 + spinnerWidth, box.Y + 12), title, brush, DrawTextOptions.Clip);
            if (hud.Detail is not null)
            {
                brush.Color = WithA(fg, .75f);
                rt.DrawTextLayout(new Vector2(box.X + 12 + spinnerWidth, box.Y + 18 + titleHeight), subtitle, brush, DrawTextOptions.Clip);
            }
        }
        finally { rt.PopAxisAlignedClip(); }
    }
}
