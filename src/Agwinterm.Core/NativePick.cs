using System.Text;
using System.Text.Json;

namespace Agwinterm.Core;

public sealed record PickItem(string Id, string Label, string? Subtitle = null);

/// <summary>Validated caller data only; none of these strings is a command.</summary>
public sealed record PickSpec(IReadOnlyList<PickItem> Items, string? Prompt, string? Query, bool AllowCustom)
{
    public const int MaxItems = 1000, MaxFieldLength = 4096, MaxBytes = 1024 * 1024;

    public static bool DisplayText(string value) => value.Length <= MaxFieldLength &&
        !value.Any(c => c < 32 || c == 127);

    public static PickSpec Parse(JsonElement args)
    {
        if (args.ValueKind != JsonValueKind.Object || Encoding.UTF8.GetByteCount(args.GetRawText()) > MaxBytes)
            throw new ArgumentException("pick.open requires an object of at most 1 MiB");
        if (!args.TryGetProperty("items", out var input) || input.ValueKind != JsonValueKind.Array)
            throw new ArgumentException("pick.open requires an items array");
        if (input.GetArrayLength() > MaxItems) throw new ArgumentException("too many items (max 1000)");
        bool custom = Boolean(args, "allowCustom");
        var items = new List<PickItem>(); var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in input.EnumerateArray())
        {
            string id = Text(item, "id", required: true)!;
            string label = Text(item, "label", required: true)!;
            string? subtitle = Text(item, "subtitle");
            if (!ids.Add(id)) throw new ArgumentException("pick item ids must be unique");
            if (label.Length == 0 || !DisplayText(label) || (subtitle is not null && !DisplayText(subtitle)))
                throw new ArgumentException("pick labels must be nonempty; display text must not contain controls");
            items.Add(new(id, label, subtitle));
        }
        if (items.Count == 0 && !custom) throw new ArgumentException("pick.open requires items or allowCustom");
        string? prompt = Text(args, "prompt"), query = Text(args, "query");
        if ((prompt is not null && !DisplayText(prompt)) || (query is not null && !DisplayText(query)))
            throw new ArgumentException("pick prompt/query must not contain controls");
        return new(items.AsReadOnly(), prompt, query, custom);
    }

    public static bool Boolean(JsonElement args, string name)
    {
        if (!args.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return false;
        if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new ArgumentException("pick " + name + " must be a boolean");
        return value.GetBoolean();
    }

    private static string? Text(JsonElement item, string name, bool required = false)
    {
        if (item.ValueKind != JsonValueKind.Object) throw new ArgumentException("pick item must be an object");
        if (!item.TryGetProperty(name, out var v) || v.ValueKind == JsonValueKind.Null)
        {
            if (required) throw new ArgumentException("pick item requires " + name);
            return null;
        }
        if (v.ValueKind != JsonValueKind.String) throw new ArgumentException("pick " + name + " must be a string");
        string text = v.GetString()!;
        if (text.Length > MaxFieldLength) throw new ArgumentException("pick field exceeds 4096 UTF-16 units");
        return text;
    }
}

public sealed record PickOutcome(string Result, string? Id = null, string? Label = null, int? Index = null, string? Query = null)
{
    public Dictionary<string, object> Wire()
    {
        var data = new Dictionary<string, object> { ["result"] = Result };
        if (Id is not null) data["id"] = Id;
        if (Label is not null) data["label"] = Label;
        if (Index is not null) data["index"] = Index.Value;
        if (Query is not null) data["query"] = Query;
        return data;
    }
}

/// <summary>UI-thread query/selection state. Filtering never searches consequence subtitles.</summary>
public sealed class PickSelection
{
    public PickSpec Spec { get; }
    public string Query { get; private set; } = "";
    public IReadOnlyList<int> Matches { get; private set; } = Array.Empty<int>();
    public int Selected { get; private set; }
    public bool Custom => Spec.AllowCustom && Query.Trim().Length > 0 && Matches.Count == 0;
    public int Count => Matches.Count + (Custom ? 1 : 0);
    public PickSelection(PickSpec spec) { Spec = spec; SetQuery(spec.Query ?? ""); }

    public void SetQuery(string query)
    {
        if (!PickSpec.DisplayText(query)) throw new ArgumentException("invalid picker query");
        Query = query; Selected = 0;
        string trimmed = query.Trim();
        var indices = Enumerable.Range(0, Spec.Items.Count);
        Matches = (trimmed.Length == 0 ? indices : indices.Select(i => (Index: i, Score: Score(trimmed, Spec.Items[i].Label)))
            .Where(x => x.Score.HasValue).OrderBy(x => x.Score).ThenBy(x => Spec.Items[x.Index].Label, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.Index).Select(x => x.Index)).ToArray();
    }

    public void Select(int index) => Selected = Math.Clamp(index, 0, Math.Max(0, Count - 1));
    public void Move(int delta) => Select(Selected + delta);
    public PickOutcome? Choose()
    {
        if (Custom) return new("custom", Query: Query.Trim());
        if (Matches.Count == 0) return null;
        int index = Matches[Selected]; var item = Spec.Items[index];
        return new("picked", item.Id, item.Label, index);
    }

    public static int? Score(string query, string label)
    {
        string text = label.ToLowerInvariant(); int total = 0;
        foreach (string term in query.ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            if (text.StartsWith(term, StringComparison.Ordinal)) continue;
            int offset = text.IndexOf(term, StringComparison.Ordinal);
            if (offset >= 0) { total += 5 + Math.Min(offset, 34); continue; }
            int cursor = 0;
            foreach (char c in text) if (cursor < term.Length && c == term[cursor]) cursor++;
            if (cursor != term.Length) return null;
            total += 40 + text.Length - term.Length;
        }
        return total;
    }
}

public sealed record PendingPick(string Id, string Window, PickSpec Spec);
public sealed record LocatedPick(string Window, PickOutcome Outcome);

/// <summary>Thread-safe bounded results; callers perform all native UI work outside this lock.</summary>
public sealed class PickRegistry
{
    private sealed record Answer(string Id, string Window, PickOutcome Outcome, long Sequence);
    private sealed class WindowState { public PendingPick? Pending; public List<Answer> Answers = new(); }
    private readonly object _gate = new();
    private readonly Dictionary<string, WindowState> _windows = new(StringComparer.Ordinal);
    private readonly List<Answer> _closed = new();
    private long _sequence;

    public PendingPick? Open(string window, PickSpec spec)
    {
        lock (_gate)
        {
            if (!_windows.TryGetValue(window, out var state)) _windows[window] = state = new();
            if (state.Pending is not null) return null;
            return state.Pending = new(Guid.NewGuid().ToString(), window, spec);
        }
    }

    public LocatedPick? Find(string id)
    {
        lock (_gate)
        {
            foreach (var state in _windows.Values)
            {
                if (state.Pending is { } pending && pending.Id == id) return new(pending.Window, new("pending"));
                if (state.Answers.FirstOrDefault(a => a.Id == id) is { } answer) return new(answer.Window, answer.Outcome);
            }
            return _closed.FirstOrDefault(a => a.Id == id) is { } old ? new(old.Window, old.Outcome) : null;
        }
    }

    public bool Resolve(string id, PickOutcome outcome)
    {
        if (outcome.Result is not ("picked" or "custom" or "cancelled")) throw new ArgumentException("terminal outcome required");
        lock (_gate)
        {
            var state = _windows.Values.FirstOrDefault(s => s.Pending?.Id == id);
            if (state?.Pending is not { } pending) return false;
            state.Answers.Add(new(id, pending.Window, outcome, ++_sequence));
            if (state.Answers.Count > 8) state.Answers.RemoveAt(0);
            state.Pending = null;
            return true;
        }
    }

    public void CloseWindow(string window)
    {
        lock (_gate)
        {
            if (!_windows.TryGetValue(window, out var state)) return;
            if (state.Pending is { } pending) Resolve(pending.Id, new("cancelled"));
            _closed.AddRange(state.Answers); _windows.Remove(window);
            _closed.Sort((a, b) => a.Sequence.CompareTo(b.Sequence));
            if (_closed.Count > 32) _closed.RemoveRange(0, _closed.Count - 32);
        }
    }
}
