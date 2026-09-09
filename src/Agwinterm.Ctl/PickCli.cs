using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using Agwinterm.Core;

namespace Agwinterm.Ctl;

/// <summary>Picker-specific payload output and exact-id waiting; no shell execution.</summary>
public static class PickCli
{
    private sealed record Options(string Verb, string? Id, Dictionary<string, string> Values, HashSet<string> Flags);
    private static readonly UTF8Encoding Utf8 = new(false, true);

    private static Options Parse(string[] argv)
    {
        var values = new Dictionary<string, string>(); var flags = new HashSet<string>();
        var positionals = new List<string>();
        for (int i = 1; i < argv.Length; i++)
        {
            string word = argv[i];
            if (!word.StartsWith("--", StringComparison.Ordinal)) { positionals.Add(word); continue; }
            string key = word[2..];
            if (key is "allow-custom" or "follow" or "no-block" or "json")
            { if (!flags.Add(key)) throw new ArgumentException("duplicate picker option: " + word); continue; }
            if (key is not ("prompt" or "query" or "window" or "pipe" or "socket" or "input-format")) throw new ArgumentException("unknown picker option: " + word);
            if (++i >= argv.Length || argv[i].StartsWith("--", StringComparison.Ordinal) || !values.TryAdd(key, argv[i]))
                throw new ArgumentException("missing or duplicate picker option: " + word);
        }
        string verb = positionals.FirstOrDefault() ?? "open";
        if (verb is not ("open" or "result" or "cancel")) throw new ArgumentException("pick accepts open, result or cancel");
        if ((verb == "open" && positionals.Count > 1) || (verb != "open" && positionals.Count != 2))
            throw new ArgumentException("pick result/cancel require one exact id; open reads items from stdin");
        if (values.TryGetValue("window", out var window) && window.Length == 0) throw new ArgumentException("empty window selector");
        if (verb != "open" && (flags.Any(f => f != "json") || values.ContainsKey("prompt") || values.ContainsKey("query") || values.ContainsKey("input-format")))
            throw new ArgumentException("open-only picker option on result/cancel");
        if (values.ContainsKey("pipe") && values.ContainsKey("socket")) throw new ArgumentException("choose --pipe or --socket");
        if (values.GetValueOrDefault("input-format", "auto") is not ("auto" or "lines" or "json")) throw new ArgumentException("input-format must be auto, lines or json");
        return new(verb, verb == "open" ? null : positionals[1], values, flags);
    }

    public static PickItem[] ParseItems(byte[] input, string format = "auto")
    {
        if (input.Length > PickSpec.MaxBytes) throw new ArgumentException("picker stdin exceeds 1 MiB");
        string text = Utf8.GetString(input);
        if (format == "json" || (format == "auto" && text.TrimStart(' ', '\t', '\r', '\n').StartsWith('[')))
        {
            using var items = JsonDocument.Parse(text);
            using var args = JsonDocument.Parse("{\"allowCustom\":true,\"items\":" + items.RootElement.GetRawText() + "}");
            return PickSpec.Parse(args.RootElement).Items.ToArray();
        }
        var result = new List<PickItem>(); var ids = new HashSet<string>(StringComparer.Ordinal);
        using var lines = new StringReader(text);
        while (lines.ReadLine() is { } line)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            if (result.Count == PickSpec.MaxItems) throw new ArgumentException("too many items (max 1000)");
            if (!PickSpec.DisplayText(line) || !ids.Add(line)) throw new ArgumentException("invalid or duplicate plain-line item");
            result.Add(new(line, line));
        }
        return result.ToArray();
    }

    private static string Request(string verb, string? id = null, string? window = null, object? args = null)
    {
        var data = new Dictionary<string, object> { ["cmd"] = "pick." + verb };
        if (id is not null) data["target"] = id;
        if (window is not null) data["window"] = window;
        if (args is not null) data["args"] = args;
        return JsonSerializer.Serialize(data);
    }

    public static int Execute(string[] argv, byte[] input, Func<string, JsonElement> send, Action<int> wait,
        Action<string> output, Action<string> error, CancellationToken cancellation = default)
    {
        string? pending = null; bool terminal = false;
        Options options;
        try { options = Parse(argv); }
        catch (Exception ex) { error(ex.Message); return 2; }
        try
        {
            cancellation.ThrowIfCancellationRequested();
            string? window = options.Values.GetValueOrDefault("window");
            if (options.Verb != "open")
            {
                var answer = send(Request(options.Verb, options.Id, window));
                var result = Success(answer);
                if (options.Verb == "cancel") { output(options.Flags.Contains("json") ? answer.GetRawText() : "cancelled"); return 0; }
                var outcome = Outcome(result); output(outcome.GetRawText()); return ExitCode(outcome);
            }
            object[] items;
            try { items = ParseItems(input, options.Values.GetValueOrDefault("input-format", "auto")).Select(i => (object)new { id = i.Id, label = i.Label, subtitle = i.Subtitle }).ToArray(); }
            catch (Exception ex) { error(ex.Message); return 2; }
            var arguments = new { items, prompt = options.Values.GetValueOrDefault("prompt"), query = options.Values.GetValueOrDefault("query"),
                allowCustom = options.Flags.Contains("allow-custom"), follow = options.Flags.Contains("follow") };
            // Same validation as the server before connecting, including line-input duplicate IDs.
            using (var check = JsonDocument.Parse(JsonSerializer.Serialize(arguments)))
            {
                try { PickSpec.Parse(check.RootElement); }
                catch (Exception ex) { error(ex.Message); return 2; }
            }
            var opened = Success(send(Request("open", window: window, args: arguments)));
            if (!opened.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(id.GetString()))
                throw new InvalidOperationException("pick.open result missing id");
            pending = id.GetString();
            if (options.Flags.Contains("no-block")) { output(JsonSerializer.Serialize(new { id = pending })); terminal = true; return 0; }
            int polls = 0;
            while (true)
            {
                cancellation.ThrowIfCancellationRequested();
                var outcome = Outcome(Success(send(Request("result", pending)))); // intentionally no window selector
                if (outcome.GetProperty("result").GetString() != "pending")
                { terminal = true; output(outcome.GetRawText()); return ExitCode(outcome); }
                wait(++polls <= 10 ? 100 : 500);
            }
        }
        catch (Exception ex) { error(ex is OperationCanceledException ? "picker wait cancelled" : ex.Message); return 1; }
        finally
        {
            if (pending is not null && !terminal)
                try { send(Request("cancel", pending)); } catch { /* best effort, preserve original failure */ }
        }
    }

    private static JsonElement Success(JsonElement response)
    {
        if (!response.TryGetProperty("ok", out var ok) || ok.ValueKind != JsonValueKind.True)
            throw new InvalidOperationException(response.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String ? error.GetString() : "invalid picker reply");
        if (!response.TryGetProperty("result", out var result)) throw new InvalidOperationException("picker reply missing result");
        return result;
    }

    private static JsonElement Outcome(JsonElement result)
    {
        if (!result.TryGetProperty("pick", out var pick) || !pick.TryGetProperty("result", out var state) || state.ValueKind != JsonValueKind.String ||
            state.GetString() is not ("pending" or "picked" or "custom" or "cancelled")) throw new InvalidOperationException("invalid pick.result outcome");
        if (state.GetString() == "picked" &&
            (!pick.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String ||
             !pick.TryGetProperty("label", out var label) || label.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(label.GetString()) ||
             !pick.TryGetProperty("index", out var index) || !index.TryGetInt32(out int n) || n < 0))
            throw new InvalidOperationException("invalid picked item");
        if (state.GetString() == "custom" && (!pick.TryGetProperty("query", out var query) || query.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(query.GetString())))
            throw new InvalidOperationException("invalid custom query");
        return pick;
    }
    private static int ExitCode(JsonElement outcome) => outcome.GetProperty("result").GetString() switch
    { "picked" or "custom" => 0, "cancelled" => 2, _ => 1 };

    public static int Run(string[] argv)
    {
        using var stop = new CancellationTokenSource();
        ConsoleCancelEventHandler handler = (_, e) => { e.Cancel = true; stop.Cancel(); };
        Console.CancelKeyPress += handler;
        try
        {
            Options options;
            try { options = Parse(argv); } catch (Exception ex) { Console.Error.WriteLine(ex.Message); return 2; }
            string pipe = options.Values.GetValueOrDefault("pipe") ?? options.Values.GetValueOrDefault("socket") ?? Environment.GetEnvironmentVariable("AGWINTERM_PIPE") ?? "agwinterm";
            byte[] input = Array.Empty<byte>();
            if (options.Verb == "open")
            {
                using var data = new MemoryStream(); var chunk = new byte[8192]; using var stdin = Console.OpenStandardInput();
                int count;
                while ((count = stdin.ReadAsync(chunk.AsMemory(), stop.Token).AsTask().WaitAsync(stop.Token).GetAwaiter().GetResult()) > 0)
                { stop.Token.ThrowIfCancellationRequested(); if (data.Length + count > PickSpec.MaxBytes) throw new ArgumentException("picker stdin exceeds 1 MiB"); data.Write(chunk, 0, count); }
                input = data.ToArray();
            }
            return Execute(argv, input, request => Send(pipe, request), ms =>
            { if (stop.Token.WaitHandle.WaitOne(ms)) stop.Token.ThrowIfCancellationRequested(); }, Console.WriteLine, Console.Error.WriteLine, stop.Token);
        }
        catch (Exception ex) { Console.Error.WriteLine(ex.Message); return ex is ArgumentException ? 2 : 1; }
        finally { Console.CancelKeyPress -= handler; }
    }

    private static JsonElement Send(string pipeName, string request)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        pipe.ConnectAsync(3000, deadline.Token).GetAwaiter().GetResult();
        using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        using var reader = new StreamReader(pipe, Utf8, false, leaveOpen: true);
        // Bound the operation without using cancellation on NamedPipe's write path (#118).
        // Disposal closes the owned handle if the deadline interrupts this wait.
        writer.WriteLineAsync(request.AsMemory(), CancellationToken.None).WaitAsync(deadline.Token).GetAwaiter().GetResult();
        string? line = reader.ReadLineAsync(deadline.Token).AsTask().GetAwaiter().GetResult();
        if (line is null) throw new IOException("picker connection closed without a reply");
        using var document = JsonDocument.Parse(line); return document.RootElement.Clone();
    }
}
