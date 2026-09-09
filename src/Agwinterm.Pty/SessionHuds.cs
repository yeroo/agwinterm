using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Agwinterm.Pty;

public sealed record HudSpec(string Message, string? Detail, string Spinner, string? BackgroundColor,
    string? TextColor, int? SizePercent, string Position);

/// <summary>Host-independent HUD validation, read-back and bounded nine-anchor geometry.</summary>
public static class SessionHuds
{
    public const int MaxTextLength = 256;
    public static readonly string[] Positions = ["top-left", "top-center", "top-right", "center-left",
        "center", "center-right", "bottom-left", "bottom-center", "bottom-right"];
    public static readonly string[] Spinners = ["none", "bar", "braille", "circle", "blocks", "dot"];
    public static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static bool TryParse(string action, JsonElement args, out HudSpec? spec, out string? error)
    {
        spec = null; error = null;
        if (action is not ("open" or "update" or "close")) { error = "hud: expected open, update or close"; return false; }
        if (args.ValueKind is not (JsonValueKind.Object or JsonValueKind.Undefined))
        { error = "hud: args must be an object"; return false; }
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        int? width = null;
        if (args.ValueKind == JsonValueKind.Object)
        foreach (var field in args.EnumerateObject())
        {
            if (action == "close") { error = "hud close takes no display options"; return false; }
            if (field.Name == "size-percent")
            {
                var raw = field.Value.ValueKind == JsonValueKind.String ? field.Value.GetString() : field.Value.GetRawText();
                if (!int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out int n) || n is < 1 or > 100)
                { error = "hud: size-percent must be a whole number in 1..100"; return false; }
                width = Math.Clamp(n, 10, 80); continue;
            }
            if (field.Name is not ("message" or "detail" or "spinner" or "color" or "text-color" or "position"))
            { error = "hud: unknown option '" + field.Name + "'"; return false; }
            if (field.Value.ValueKind != JsonValueKind.String)
            { error = "hud: '" + field.Name + "' must be text"; return false; }
            values[field.Name] = field.Value.GetString()!;
        }
        if (action == "close") return true;
        string Read(string key, string fallback = "") => values.GetValueOrDefault(key, fallback);
        string message = Read("message"), detail = Read("detail");
        if (string.IsNullOrWhiteSpace(message)) { error = "hud requires a nonblank message"; return false; }
        foreach (var value in new[] { message, detail })
        {
            if (value.Any(char.IsControl)) { error = "hud text must not contain control characters"; return false; }
            // Normalize validates UTF-16 too; malformed Unicode is a refusal, never a render exception.
            try
            {
                if (value.Normalize().EnumerateRunes().Count() > MaxTextLength)
                { error = "hud text exceeds 256 Unicode scalars"; return false; }
            }
            catch (ArgumentException) { error = "hud text is not valid Unicode"; return false; }
        }
        string position = Read("position", "center") switch { "top" => "top-center", "bottom" => "bottom-center", var p => p };
        if (!Positions.Contains(position)) { error = "hud: invalid position; use " + string.Join('|', Positions) + " (top/bottom aliases accepted)"; return false; }
        string spinner = Read("spinner", "none");
        if (!Spinners.Contains(spinner)) { error = "hud: invalid spinner; use " + string.Join('|', Spinners); return false; }
        foreach (string key in new[] { "color", "text-color" })
        {
            if (!values.TryGetValue(key, out string? color)) continue;
            string digits = color.StartsWith('#') ? color[1..] : color;
            if (digits.Length != 6 || !digits.All(char.IsAsciiHexDigit))
            { error = "hud: " + key + " must be #rrggbb"; return false; }
            values[key] = "#" + digits.ToLowerInvariant();
        }
        spec = new(message.Normalize(), string.IsNullOrEmpty(detail) ? null : detail.Normalize(), spinner,
            values.GetValueOrDefault("color"), values.GetValueOrDefault("text-color"), width, position);
        return true;
    }

    public static string Reply(string session, HudSpec? hud) => JsonSerializer.Serialize(new { session, hud }, JsonOptions);
    public readonly record struct Box(float X, float Y, float Width, float Height);
    public static Box Place(float x, float y, float width, float height, float neededWidth, float neededHeight, HudSpec spec)
    {
        width = Math.Max(0, width); height = Math.Max(0, height);
        float w = spec.SizePercent is { } n ? width * n / 100 : Math.Clamp(neededWidth, width * .1f, width * .8f);
        float h = Math.Clamp(neededHeight, 0, height * .8f);
        int i = Array.IndexOf(Positions, spec.Position); if (i < 0) i = 4;
        static float Offset(float extent, float size, int band) => band switch
        { 0 => extent * .1f, 2 => extent * .9f - size, _ => (extent - size) / 2 };
        return new(x + Offset(width, w, i % 3), y + Offset(height, h, i / 3), w, h);
    }

    public static string Frame(string spinner, long milliseconds)
    {
        string[] frames = spinner switch {
            "bar" => ["|", "/", "-", "\\"], "braille" => ["⠋", "⠙", "⠹", "⠸", "⠼", "⠴", "⠦", "⠧", "⠇", "⠏"],
            "circle" => ["◐", "◓", "◑", "◒"], "blocks" => ["▁", "▃", "▄", "▅", "▆", "▇", "▆", "▅", "▄", "▃"],
            "dot" => ["●", " "], _ => [""] };
        int interval = spinner switch { "braille" or "blocks" => 80, "circle" => 120, "dot" => 450, _ => 100 };
        return frames[(int)((Math.Max(0, milliseconds) / interval) % frames.Length)];
    }
}
