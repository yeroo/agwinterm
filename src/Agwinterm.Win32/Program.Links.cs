using Agwinterm.Pty;
using static Agwinterm.Win32.Win32;

namespace Agwinterm.Win32;

/// <summary>
/// The session link (<see cref="SessionLinks"/> holds the rules): set from the sidebar's context
/// menu, File ▸ Session Link ▸ Set Session Link…, the Command Palette or <c>session link</c>; opened by
/// <c>open_session_link</c> (Ctrl+Shift+L), the link glyph left of the session's name, or
/// <c>session link --open</c>.
/// </summary>
internal partial class Program
{
    /// <summary><c>_editing</c>'s value while the inline field edits <paramref name="Ses"/>'s link.</summary>
    private sealed record LinkEdit(Ses Ses);

    /// <summary>Segoe Fluent Icons "Link": drawn left of a linked session's name.</summary>
    private const string GlyphLink = "";

    /// <summary>Open the session's link in the default browser (http/https only, via <see cref="OpenLink"/>).</summary>
    private void OpenSessionLink(Ses ses)
    {
        if (ses.Link is not { } link) { ShowToast("no link on this session — Set Session Link… adds one"); return; }
        OpenLink(link);
    }

    /// <summary>Set or (null) clear the link, already validated, then repaint and save.</summary>
    private void SetSessionLink(Ses ses, string? link)
    {
        ses.Link = link;
        RequestRedraw();
        SaveState();
        ShowToast(link is null ? "session link cleared" : "session link: " + link);
    }

    /// <summary>
    /// The inline field for the link, over the session's sidebar row (the title bar when the row is not
    /// on screen and the session is the active one). It starts with the current link; with none, with
    /// the clipboard when that holds a valid link, else empty. Enter commits, Esc cancels, an empty
    /// field clears the link.
    /// </summary>
    private void StartSidebarLinkEdit(Ses ses)
    {
        if (_sidebarW <= 0) ToggleSidebar();
        string text = ses.Link ?? "";
        if (text.Length == 0)
        {
            string clip = ClipboardGet();
            if (SessionLinks.IsValid(clip)) text = SessionLinks.Normalize(clip);
        }
        var editing = new LinkEdit(ses);
        StartRowEdit(ses, editing, text);
        if (ReferenceEquals(_editing, editing)) return;
        if (ReferenceEquals(ses, _active) && _titleTextRect.Width > 0) { StartTitleEdit(editing, text); return; }
        ShowToast("the session's row is not visible; select the session and try again");
    }

    /// <summary>Commit the inline field: blank clears, a valid URL is set, anything else is refused
    /// with the rule it broke and the old link kept.</summary>
    private void CommitLinkEdit(Ses ses, string raw)
    {
        if (!AllSessions().Contains(ses)) return;   // closed while the field was up
        if (string.IsNullOrWhiteSpace(raw))
        {
            if (ses.Link is not null) SetSessionLink(ses, null);
            return;
        }
        if (!SessionLinks.TryNormalize(raw, out string link, out string? refusal))
        {
            ShowToast(refusal!, 4000);
            return;
        }
        if (link != ses.Link) SetSessionLink(ses, link);
    }
}
