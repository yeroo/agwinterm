namespace Agwinterm.Pty;

/// <summary>
/// How <c>session.new</c> answers a workspace it cannot place a session in (P2, "stop lying to the
/// caller" — decision 1 of the parity programme: <b>refuse</b>).
///
/// Before P2 an unknown <c>--workspace</c> id, or an unknown <c>--workspace-name</c> without
/// <c>--create-workspace</c>, silently fell back to the ACTIVE workspace and answered ok:true with a
/// session id — the caller believed it had placed a session somewhere it had not. Every other
/// workspace-taking verb (<c>session.to-workspace</c>, <c>workspace.select</c>,
/// <c>workspace.delete</c>) already refused, agliteterm already refused on <c>session.new</c>, and
/// the conformance contract already states the rule in prose for <c>workspace.select</c>. This is
/// <c>session.new</c> obeying it.
///
/// The wording lives here, shared by the app's host and the test fake, so a refusal reads the same
/// from both — and so the fake cannot drift into accepting what the app refuses (or the reverse),
/// which is the double-drift P1's verification found in <c>Resolve</c>.
/// </summary>
public static class SessionNewWorkspaces
{
    /// <summary><c>--workspace</c> matched neither a workspace id nor an id prefix. Names the value,
    /// how to list the real ones, and the flag pair that creates one (#214: a refusal names its
    /// escape hatch).</summary>
    public static string UnknownId(string workspace) =>
        $"session.new: no workspace has id or id-prefix '{workspace}'. `tree --json` lists them; " +
        "`--workspace-name NAME --create-workspace` creates one. No session was created.";

    /// <summary><c>--workspace-name</c> matched no workspace and <c>--create-workspace</c> was not
    /// given. The caller who hits this almost always meant to create it, so the flag is named.</summary>
    public static string UnknownName(string workspaceName) =>
        $"session.new: no workspace is named '{workspaceName}'. Add --create-workspace to create it " +
        "and place the session there. No session was created.";

    /// <summary><c>--workspace</c> and <c>--workspace-name</c> together. Two answers to "where?"
    /// are refused rather than ranked (before P2 <c>--workspace</c> silently won by being tested
    /// first) — the same rule as <c>--stdin</c> beside positional text on <c>session type</c>.</summary>
    public static string TwoSources(string workspace, string workspaceName) =>
        $"session.new: --workspace '{workspace}' and --workspace-name '{workspaceName}' are two answers " +
        "to one question; pass one of them. No session was created.";
}
