using System.Globalization;
using System.Text.Json;

namespace Agwinterm.Ctl;

/// <summary>
/// Builds the <c>image.frameshm</c> control args from parsed CLI positionals and options.
/// Split out of <c>Program.cs</c> (top-level statements, so not directly testable) because the
/// numeric handling is the part that has to be right: <c>ControlServer</c> rejects a string where
/// a number belongs, so every option is parsed here and placed into <c>cargs</c> as an int.
/// The wire shape is <c>docs/specs/image-frameshm.md</c>.
/// </summary>
public static class FrameShmCli
{
    /// <summary>Literal prefix every mapping name must carry; see the spec's "Mapping name".</summary>
    public const string NamePrefix = @"Local\agwinterm-frame-";

    /// <summary>
    /// Every numeric field of an <c>images[]</c> entry, in the order the spec lists them. All are
    /// optional; an omitted one is left out entirely so the server applies its own default.
    /// </summary>
    public static readonly string[] NumericFields =
    [
        "id", "slot", "seq", "width", "height", "stride", "format",
        "row", "col", "cols", "rows", "sx", "sy", "sw", "sh",
    ];

    /// <summary>
    /// Builds <c>args</c> for <c>image.frameshm</c> from <paramref name="rest"/> (the positionals
    /// after <c>image frameshm</c>) and <paramref name="options"/> (the parsed <c>--flags</c>).
    /// Returns false with a user-facing <paramref name="error"/> rather than throwing, so the CLI
    /// can fail before opening the pipe.
    /// </summary>
    public static bool TryBuildArgs(IReadOnlyList<string> rest, IReadOnlyDictionary<string, string> options,
                                    out Dictionary<string, object?> cargs, out string? error)
    {
        cargs = new Dictionary<string, object?>();
        error = null;

        // --images is the escape hatch for the multi-entry case: the verb applies a whole images[]
        // array all-or-nothing, and a composition of several mappings has no flag shape.
        if (options.TryGetValue("images", out string? rawImages) && rawImages != "true")
        {
            if (rest.Count > 0) { error = "takes either <name> or --images, not both"; return false; }
            JsonElement arr;
            try { arr = JsonDocument.Parse(rawImages).RootElement.Clone(); }
            catch (JsonException ex) { error = "--images is not valid JSON: " + ex.Message; return false; }
            if (arr.ValueKind != JsonValueKind.Array) { error = "--images must be a JSON array"; return false; }
            cargs["images"] = arr;
            return true;
        }

        if (rest.Count == 0) { error = "needs a mapping name (or --images)"; return false; }
        if (rest.Count > 1) { error = $"takes one mapping name (got {rest.Count}); use --images for several"; return false; }

        string name = rest[0];
        if (!name.StartsWith(NamePrefix, StringComparison.Ordinal))
        { error = $"mapping name must start with '{NamePrefix}' (got '{name}')"; return false; }

        var img = new Dictionary<string, object?> { ["name"] = name };
        foreach (string field in NumericFields)
        {
            if (!options.TryGetValue(field, out string? raw) || raw == "true") continue;
            if (!long.TryParse(raw, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long v))
            { error = $"--{field} must be a whole number (got '{raw}')"; return false; }
            // seq is the one field the server reads as a 64-bit publish counter; the rest are ints,
            // and catching the overflow here names the flag instead of the JSON property.
            if (field == "seq") { img[field] = v; continue; }
            if (v is < int.MinValue or > int.MaxValue)
            { error = $"--{field} is out of range for a 32-bit integer (got '{raw}')"; return false; }
            img[field] = (int)v;
        }

        cargs["images"] = new[] { img };
        return true;
    }
}
