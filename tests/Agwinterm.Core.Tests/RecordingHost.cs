namespace Agwinterm.Core.Tests;

/// <summary>Test fake for the emulator's host-action seam: records every action for assertions.
/// Attach with <c>emulator.Host = host</c>.</summary>
public sealed class RecordingHost : IHostActions
{
    public readonly List<(string Title, string Body)> Notifications = new();
    public readonly List<(int State, int Value)> ProgressReports = new();
    public readonly List<string> ClipboardWrites = new();
    public readonly List<string> Responses = new();
    public readonly List<(string Kind, string Detail)> Unhandleds = new();

    public void Notify(string title, string body) => Notifications.Add((title, body));
    public void Progress(int state, int value) => ProgressReports.Add((state, value));
    public void ClipboardWrite(string text) => ClipboardWrites.Add(text);
    public void Respond(string reply) => Responses.Add(reply);
    public void Unhandled(string kind, string detail) => Unhandleds.Add((kind, detail));
}
