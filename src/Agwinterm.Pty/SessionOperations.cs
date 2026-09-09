namespace Agwinterm.Pty;

/// <summary>Shared validation for protection, MRU operations and durable agent commands.</summary>
public static class SessionOperations
{
    public static bool IsReadOnlyOp(string op) => op is "on" or "off" or "toggle" or "state" or "get";
    public static bool IsSwitchOp(string op) => op is "begin" or "advance" or "next"
        or "advance-back" or "back" or "prev" or "previous" or "commit" or "cancel";
    public static string UnknownOp(string op) => ISessionHost.RefusePrefix + $"unknown op '{op}'; nothing changed";
    public static string? Binding(string agent) => string.IsNullOrWhiteSpace(agent)
        || agent.Equals("none", StringComparison.OrdinalIgnoreCase) ? null : agent;
}
