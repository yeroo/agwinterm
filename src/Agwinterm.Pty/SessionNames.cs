using System.Text.Json;

namespace Agwinterm.Pty;

/// <summary>
/// The rules for a session's NAME — the custom label <c>session rename</c> writes, shown in the
/// sidebar row and the title bar, carried in <c>tree --json</c> as <c>name</c>, and restored after a
/// restart. Kept here, not in the Win32 assembly, for the reason <see cref="SessionContexts"/> is:
/// the control server refuses a bad value before the host is reached, the fake host exercises the
/// same refusal, and both hosts build one reply shape that cannot drift between them.
///
/// <para><b>A rename names a SESSION, and says which one</b> (agwinterm #287). The target resolves
/// the way every content verb's does — exact pane, exact session, pane prefix, session prefix /
/// name — so a target that names a PANE (a split pane's id, a scratch or overlay cover's id) lands
/// on the session that pane belongs to. That is not a widening the caller has to guess at: a program
/// running in a pane is handed that PANE's id as <c>AGWINTERM_SESSION_ID</c>, and the CLI sends it as
/// the target when none is passed, so a bare <c>session rename &lt;name&gt;</c> from inside a split
/// pane IS a pane-targeted rename, and the session it belongs to is the only thing in the picture
/// with a name — a pane has no label of its own in the sidebar, and no name field in the state
/// file. Refusing it would refuse the commonest call the verb has.</para>
///
/// <para><b>What was wrong was the silence.</b> The reply was the constant <c>"renamed"</c>, so
/// which session took the name was unknowable from the caller's side — the defect P2 fixed one verb
/// over on <c>session.restore</c> ("before P2 the reply was the constant <c>pinned</c>"), and the
/// shape P3 shipped on <c>session.context</c>. The reply is now <see cref="Reply"/>:
/// <c>{"session":"&lt;id&gt;","name":"&lt;name&gt;"}</c> — the session the name landed on and the
/// name IN EFFECT, read back off the session after the write rather than echoed from the request.
/// A caller that passed a pane id can see the session it hit, and one that meant a different session
/// can see that it did.</para>
///
/// <para><b>A target that owns no session is refused</b>, unchanged: agwinterm's window-level quick
/// terminal covers no session, so there is nothing for a name to land on. (agliteterm refuses its
/// scratch / overlay / quick covers here for the same condition stated in its own model — a lite
/// cover is a hidden session with no owner at all, where an agwinterm cover covers one.)</para>
/// </summary>
public static class SessionNames
{
    /// <summary>The reply's key for the name, and <c>tree</c>'s key for the same value.</summary>
    public const string Key = "name";

    /// <summary>The blank refusal. A rename with no text is not a request to clear the name (there
    /// is no clear: a session with no custom name shows its cwd/OSC title, which is a different
    /// thing from a session named ""), so it is refused rather than ranked — the wording
    /// <see cref="SessionContexts.Blank"/> uses for the same condition one verb over.</summary>
    public const string Blank =
        "session rename: the name is blank; a name is one line of printable text. Nothing changed.";

    /// <summary>The host's refusal when the target resolves to no session — the same "session not
    /// found" <c>session.context</c> answers, so a script sees ONE wording for one condition across
    /// the two verbs that write to a session's labels. Reached by a target that matches nothing, and
    /// by the window-level quick terminal, which covers no session.</summary>
    public const string NoSession = "session not found; nothing changed";

    /// <summary>
    /// The success reply, built by both hosts so the shape cannot drift between them:
    /// <c>{"session":"&lt;id&gt;","name":"&lt;name&gt;"}</c>. It names the session the name landed on
    /// (the target may have been a prefix, a name, a split pane's id or a cover pane's id) and the
    /// name IN EFFECT after the write — read off the session inside the UI-thread hop, never echoed
    /// from the request, so a reply that says a name is set means the session carries it.
    /// </summary>
    public static string Reply(string sessionId, string name) =>
        "{\"session\":" + JsonSerializer.Serialize(sessionId) + ",\"" + Key + "\":" +
        JsonSerializer.Serialize(name) + "}";
}
