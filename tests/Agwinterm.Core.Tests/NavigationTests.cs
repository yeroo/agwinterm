using Agwinterm.Core;

namespace Agwinterm.Core.Tests;

public class NavigationTests
{
    [Theory]
    [InlineData("next", 1)][InlineData("prev", -1)][InlineData("previous", -1)]
    [InlineData(null, 0)][InlineData("", 0)][InlineData("up", 0)][InlineData("Next", 0)]
    public void DirectionIsExplicit(string? word, int step)
    { Assert.Equal(step != 0, WorkspaceNavigation.TryDirection(word, out int actual)); Assert.Equal(step, actual); }

    [Theory]
    [InlineData(0, -1, 1, -1)][InlineData(1, 0, 1, -1)]
    [InlineData(3, 0, -1, 2)][InlineData(3, 2, 1, 0)]
    [InlineData(3, 1, 1, 2)][InlineData(3, 1, -1, 0)]
    [InlineData(3, -1, 1, 0)][InlineData(3, -1, -1, 2)]
    [InlineData(3, 1, 0, -1)]
    public void NavigationWrapsVisibleOrder(int count, int current, int step, int expected)
        => Assert.Equal(expected, WorkspaceNavigation.Destination(count, current, step));

    [Fact] public void AlternativesExpandWithoutRemovingDefaults()
    {
        var result = Keymap.Parse("map ctrl+g | alt+g = next_workspace\nmap leader a | b = previous_workspace\nleader=f10");
        Assert.Empty(result.Diagnostics);
        Assert.Equal("next_workspace", result.Bindings["ctrl+g"]);
        Assert.Equal("next_workspace", result.Bindings["alt+g"]);
        Assert.Equal("previous_workspace", result.LeaderBindings["a"]);
        Assert.Equal("previous_workspace", result.LeaderBindings["b"]);
        Assert.Equal("new_session", result.Bindings["ctrl+shift+t"]);
        Assert.Equal("f10", result.Leader);
    }

    [Theory][InlineData("ctrl+g | bogus")][InlineData("| ctrl+g")][InlineData("ctrl+g |")][InlineData("ctrl+g || alt+g")]
    public void BadAlternativeRejectsWholeLine(string keys)
    {
        var result = Keymap.Parse($"map {keys} = next_workspace");
        Assert.Single(result.Diagnostics); Assert.False(result.Bindings.ContainsKey("ctrl+g"));
    }

    [Fact] public void CommandsKeepPipesAndLastMappingWins()
    {
        var result = Keymap.Parse("command Pipe = echo a | more\nmap f6 | f7 = command:Pipe\nmap f6=toggle_workspace_collapse");
        Assert.Empty(result.Diagnostics); Assert.Equal("echo a | more", Assert.Single(result.Commands).Text);
        Assert.Equal("command:Pipe", result.Bindings["f7"]);
        Assert.Equal("toggle_workspace_collapse", result.Bindings["f6"]);
    }

    [Fact] public void LeaderIsStillExactlyOneChord()
    { var result = Keymap.Parse("leader=f6|f7"); Assert.Null(result.Leader); Assert.Single(result.Diagnostics); }

    [Theory]
    [InlineData("CMD.EXE", false, true, "cmd")][InlineData("pwsh.exe", false, true, "pwsh")]
    [InlineData("powershell.exe", false, true, "powershell")][InlineData("bash", false, true, "bash")]
    [InlineData("cmd.exe", true, true, null)][InlineData("cmd.exe", false, false, null)]
    [InlineData("python.exe", false, true, null)][InlineData(null, false, true, null)]
    public void ShellHintsAreConservative(string? name, bool children, bool live, string? expected)
        => Assert.Equal(expected, ForegroundShellNames.Recognize(name, children, live));
}
