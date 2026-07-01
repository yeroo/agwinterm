namespace Agwinterm.Core;

/// <summary>
/// Push-based agent state for a session (agterm model): set explicitly by the agent
/// via the control API, never inferred from terminal output. Surfaced as a glyph/indicator.
/// </summary>
public enum AgentStatus
{
    Idle,       // no indicator
    Active,     // agent working
    Blocked,    // awaiting the user
    Completed,  // finished
}
