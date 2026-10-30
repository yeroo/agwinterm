using System.Text.Json;

namespace Agwinterm.Pty;

/// <summary>
/// Rules for a workspace's default directory for new sessions without an explicit <c>cwd</c> — persisted,
/// exposed on <c>tree</c> as <c>defaultDir</c>, set via <c>workspace.default-dir</c> / <c>workspace new --default-dir</c>.
/// </summary>
public static class WorkspaceDefaultDirs
{
    public const string Key = "defaultDir";

    public const string NoWorkspace = "workspace not found; nothing changed";

    public const string NotDirectory = "workspace default directory: the path is not an existing directory; nothing changed.";

    public const string InvalidPath = "workspace default directory: the path is invalid; nothing changed.";

    public const string TextAndClear =
        "workspace default directory: a path and --clear cannot be combined (one sets the directory, the other removes it). Nothing changed.";

    /// <summary>Normalize to a full path when setting; refuses missing directories.</summary>
    public static string? ValidateSet(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return Blank;
        try
        {
            string full = Path.GetFullPath(path.Trim());
            if (!Directory.Exists(full)) return NotDirectory;
            return null;
        }
        catch
        {
            return InvalidPath;
        }
    }

    public const string Blank =
        "workspace default directory: the path is blank; pass a directory or use --clear to remove the default. Nothing changed.";

    public static string NormalizeSet(string path) => Path.GetFullPath(path.Trim());

    public static string Reply(string workspaceId, string? defaultDir) =>
        "{\"workspace\":" + JsonSerializer.Serialize(workspaceId) + ",\"" + Key + "\":" +
        (defaultDir is null ? "null" : JsonSerializer.Serialize(defaultDir)) + "}";
}
