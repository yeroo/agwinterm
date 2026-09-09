using Agwinterm.Core;

namespace Agwinterm.Pty;

/// <summary>App-level picker routing, independent of session selectors and window liveness.</summary>
public interface IPickHost
{
    string OpenPick(PickSpec spec, string? window, bool follow);
    PickOutcome ReadPick(string id, string? window);
    void CancelPick(string id, string? window);
}
