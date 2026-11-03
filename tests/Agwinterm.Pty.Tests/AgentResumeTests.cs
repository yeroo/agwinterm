using Agwinterm.Pty;
using static Agwinterm.Pty.AgentResume;

namespace Agwinterm.Pty.Tests;

/// <summary>
/// The pure half of the SessionStart resume binding (#316): telling the pane's own agent from a nested one
/// by the process tree, the flags a relaunch keeps, and the line typed into each kind of shell. The trees
/// are the shapes observed on Windows with Git Bash, Claude Code 2.1 (native), Codex 0.156 (npm) and Devin
/// CLI 3000.11 (native).
/// </summary>
public class AgentResumeTests
{
    private const int Shell = 100;
    private const string ClaudeExe = @"C:\Users\u\.local\bin\claude.exe";
    private const string CodexJs = @"""C:\Program Files\nodejs\node.exe"" C:\Users\u\AppData\Roaming\npm/node_modules/@openai/codex/bin/codex.js";
    private const string CodexExe = @"C:\Users\u\AppData\Roaming\npm\node_modules\@openai\codex\node_modules\@openai\codex-win32-x64\vendor\x86_64-pc-windows-msvc\bin\codex.exe";
    private const string DevinExe = @"C:\Users\u\AppData\Local\devin\cli\bin\devin.exe";

    /// <summary>A chain child first: each row's parent is the next row, the last row's parent is the shell.</summary>
    private static Dictionary<int, ProcRow> Chain(params (string Name, string Cmd)[] childFirst)
    {
        var rows = new Dictionary<int, ProcRow>();
        int pid = 1;
        foreach (var (name, cmd) in childFirst)
        {
            rows[pid] = new ProcRow(pid, pid == childFirst.Length ? Shell : pid + 1, name, cmd);
            pid++;
        }
        rows[Shell] = new ProcRow(Shell, 50, "bash.exe", @"C:\Git\bin\bash.exe --login -i");
        rows[50] = new ProcRow(50, 40, "agwinterm-ptyhost.exe", "agwinterm-ptyhost.exe --pipe agwinterm-rust");
        return rows;
    }

    [Fact]
    public void ClaudeInGitBashIsFoundThroughTheHookShells()
    {
        var procs = Chain(
            ("powershell.exe", "powershell -NoProfile -File bind.ps1 claude"),
            ("bash.exe", "bash.exe -c \"powershell ...\""),
            ("claude.exe", ClaudeExe + " --dangerously-skip-permissions"),
            ("bash.exe", "usr\\bin\\bash.exe --login -i"),
            ("bash.exe", "usr\\bin\\bash.exe --login -i"));
        Assert.Equal(ClaudeExe + " --dangerously-skip-permissions", FindAgentCommandLine(procs, 1, Shell, "claude", out var why));
        Assert.Null(why);
    }

    [Fact]
    public void CodexLauncherAndBinaryCountAsOneAgentAndTheLauncherLineWins()
    {
        var procs = Chain(
            ("powershell.exe", "powershell -NoProfile -File bind.ps1 codex"),
            ("codex.exe", CodexExe + " --sandbox workspace-write"),
            ("node.exe", CodexJs + " --sandbox workspace-write"));
        Assert.Equal(CodexJs + " --sandbox workspace-write", FindAgentCommandLine(procs, 1, Shell, "codex", out _));
    }

    [Fact]
    public void AHeadlessClaudeUnderTheInteractiveOneIsNested()
    {
        var procs = Chain(
            ("powershell.exe", "powershell -NoProfile -File bind.ps1 claude"),
            ("bash.exe", "bash -c powershell"),
            ("claude.exe", ClaudeExe + " -p \"summarize\""),
            ("bash.exe", "bash -c \"claude -p summarize\""),
            ("claude.exe", ClaudeExe));
        Assert.Null(FindAgentCommandLine(procs, 1, Shell, "claude", out var why));
        Assert.StartsWith("nested", why);
    }

    [Fact]
    public void ACodexExecRunFromClaudeIsNested()
    {
        var procs = Chain(
            ("powershell.exe", "powershell -NoProfile -File bind.ps1 codex"),
            ("codex.exe", CodexExe + " exec hi"),
            ("node.exe", CodexJs + " exec hi"),
            ("bash.exe", "bash -c \"codex exec hi\""),
            ("claude.exe", ClaudeExe));
        Assert.Null(FindAgentCommandLine(procs, 1, Shell, "codex", out var why));
        Assert.Contains("under another claude", why);
    }

    [Fact]
    public void DevinIsFoundAboveItsAcpWorker()
    {
        // Observed: Devin runs its hooks through Git Bash from an `acp` worker it starts under itself.
        var procs = Chain(
            ("powershell.exe", "powershell -NoProfile -File bind.ps1 devin"),
            ("bash.exe", "bash.exe -c \"powershell ...\""),
            ("devin.exe", $"\"{DevinExe}\" acp"),
            ("devin.exe", DevinExe + " --permission-mode dangerous"));
        Assert.Equal(DevinExe + " --permission-mode dangerous", FindAgentCommandLine(procs, 1, Shell, "devin", out var why));
        Assert.Null(why);
    }

    [Theory]
    [InlineData(" -p \"fix it\"")]
    [InlineData(" --print")]
    [InlineData(" --print=hi")]
    public void ADevinPrintRunInThePaneBindsNothing(string args)
    {
        var procs = Chain(
            ("powershell.exe", "powershell -NoProfile -File bind.ps1 devin"),
            ("bash.exe", "bash.exe -c \"powershell ...\""),
            ("devin.exe", $"\"{DevinExe}\" acp"),
            ("devin.exe", DevinExe + args));
        Assert.Null(FindAgentCommandLine(procs, 1, Shell, "devin", out var why));
        Assert.StartsWith("headless", why);
    }

    [Fact]
    public void ADevinWhoseCommandLineIsUnreadableBindsNothing()
    {
        var procs = Chain(("powershell.exe", "p"), ("devin.exe", ""));
        Assert.Null(FindAgentCommandLine(procs, 1, Shell, "devin", out var why));
        Assert.Contains("could not be read", why);
    }

    [Fact]
    public void ADevinPromptThatMentionsPrintIsStillTheTui()
    {
        var procs = Chain(("powershell.exe", "p"), ("devin.exe", DevinExe + " -- explain -p"));
        Assert.Equal(DevinExe + " -- explain -p", FindAgentCommandLine(procs, 1, Shell, "devin", out _));
    }

    [Fact]
    public void ADevinPrintRunFromAnotherDevinIsNested()
    {
        var procs = Chain(
            ("powershell.exe", "powershell -NoProfile -File bind.ps1 devin"),
            ("bash.exe", "bash.exe -c \"powershell ...\""),
            ("devin.exe", $"\"{DevinExe}\" acp"),
            ("devin.exe", DevinExe + " -p hi"),
            ("bash.exe", "bash -c \"devin -p hi\""),
            ("devin.exe", $"\"{DevinExe}\" acp"),
            ("devin.exe", DevinExe));
        Assert.Null(FindAgentCommandLine(procs, 1, Shell, "devin", out var why));
        Assert.StartsWith("nested", why);
    }

    [Fact]
    public void CodexFromGitBashIsFoundBelowTheMsysExecBreak()
    {
        // Observed: bash runs the npm `codex` shim, MSYS exec leaves sh.exe with a parent that has already
        // exited, so the walk never reaches the pane's shell. The agent below the break is the pane's own.
        var procs = new Dictionary<int, ProcRow>
        {
            [1] = new(1, 2, "powershell.exe", "powershell -NoProfile -File bind.ps1 codex"),
            [2] = new(2, 3, "powershell.exe", "powershell -Command <hook>"),   // Codex runs hooks through PowerShell
            [3] = new(3, 4, "codex.exe", CodexExe + " -s workspace-write"),
            [4] = new(4, 5, "node.exe", CodexJs + " -s workspace-write"),
            [5] = new(5, 777, "sh.exe", "sh C:/npm/codex"),                 // 777: exited
            [Shell] = new(Shell, 50, "bash.exe", "bash --login -i"),
        };
        Assert.Equal(CodexJs + " -s workspace-write", FindAgentCommandLine(procs, 1, Shell, "codex", out var why));
        Assert.Null(why);
    }

    [Fact]
    public void AWalkThatEndsBeforeItsAgentBindsNothing()
    {
        var noAgent = Chain(("powershell.exe", "p"), ("bash.exe", "bash"));
        Assert.Null(FindAgentCommandLine(noAgent, 1, Shell, "claude", out var why));
        Assert.Contains("no claude process", why);

        var wrongAgent = Chain(("powershell.exe", "p"), ("codex.exe", CodexExe));
        Assert.Null(FindAgentCommandLine(wrongAgent, 1, Shell, "claude", out _));

        var hookGone = new Dictionary<int, ProcRow> { [Shell] = new(Shell, 50, "bash.exe", "bash") };
        Assert.Null(FindAgentCommandLine(hookGone, 1, Shell, "claude", out _));
    }

    [Fact]
    public void TheWalkStopsAtThePanesShell()
    {
        // An agent ABOVE the pane's shell (agwinterm itself started from a Claude session) is not the pane's.
        var procs = Chain(("powershell.exe", "p"), ("bash.exe", "bash -c hook"));
        procs[50] = new ProcRow(50, 60, "claude.exe", ClaudeExe);
        Assert.Null(FindAgentCommandLine(procs, 1, Shell, "claude", out _));
    }

    [Fact]
    public void AgentOfKnowsTheBinariesAndTheNpmLaunchers()
    {
        Assert.Equal("claude", AgentOf(new ProcRow(1, 2, "Claude.exe", "")));
        Assert.Equal("codex", AgentOf(new ProcRow(1, 2, "node.exe", CodexJs)));
        Assert.Equal("devin", AgentOf(new ProcRow(1, 2, "devin.exe", DevinExe)));
        Assert.Equal("claude", AgentOf(new ProcRow(1, 2, "node.exe", @"node C:\npm\node_modules\@anthropic-ai\claude-code\cli.js")));
        Assert.Null(AgentOf(new ProcRow(1, 2, "node.exe", "node server.js")));
        Assert.Null(AgentOf(new ProcRow(1, 2, "bash.exe", "bash -c claude")));
    }

    [Theory]
    [InlineData(ClaudeExe + " --resume 36145f11-e132-4691-ab98-ae5c4445c46b --dangerously-skip-permissions --model opus", "--dangerously-skip-permissions")]
    [InlineData(ClaudeExe + " --permission-mode acceptEdits", "--permission-mode acceptEdits")]
    [InlineData(ClaudeExe + " --permission-mode=plan", "--permission-mode plan")]
    [InlineData(ClaudeExe + " --permission-mode \"x; rm -rf ~\"", "")]   // a value a shell could interpret is dropped
    [InlineData(ClaudeExe + " \"fix the login bug\"", "")]
    public void ClaudeKeepsItsPermissionMode(string commandLine, string expected)
        => Assert.Equal(expected, string.Join(' ', ResumeFlags("claude", commandLine)));

    [Theory]
    [InlineData(" resume 01a0 --dangerously-bypass-approvals-and-sandbox -m gpt-5", "--dangerously-bypass-approvals-and-sandbox")]
    [InlineData(" -s workspace-write -a on-request", "-s workspace-write -a on-request")]
    [InlineData(" --sandbox=read-only --profile work -c model=\"o3\"", "--sandbox read-only --profile work")]
    [InlineData(" -p", "")]   // a valued flag with nothing after it
    public void CodexKeepsItsSandboxAndApprovalMode(string args, string expected)
        => Assert.Equal(expected, string.Join(' ', ResumeFlags("codex", CodexJs + args)));

    [Theory]
    [InlineData(" --permission-mode dangerous --model opus", "--permission-mode dangerous")]
    [InlineData(" -r holy-antler --permission-mode=accept-edits --sandbox", "--permission-mode accept-edits --sandbox")]
    [InlineData(" --config C:\\cfg.json", "")]
    public void DevinKeepsItsPermissionModeAndSandbox(string args, string expected)
        => Assert.Equal(expected, string.Join(' ', ResumeFlags("devin", DevinExe + args)));

    [Fact]
    public void ComposeUsesEachShellsOwnSyntax()
    {
        var yolo = new[] { "--dangerously-skip-permissions" };
        Assert.Equal("cd 'C:\\src\\it'\\''s' && claude --resume abc --dangerously-skip-permissions",
            Compose(ShellKind.Bash, "claude", "abc", @"C:\src\it's", yolo));
        Assert.Equal("Set-Location -LiteralPath 'C:\\src\\it''s'; claude --resume abc",
            Compose(ShellKind.PowerShell, "claude", "abc", @"C:\src\it's", []));
        Assert.Equal("cd /d \"C:\\src\" && codex resume 01a0 -s workspace-write",
            Compose(ShellKind.Cmd, "codex", "01a0", @"C:\src", new[] { "-s", "workspace-write" }));
        Assert.Equal("codex resume 01a0", Compose(ShellKind.Other, "codex", "01a0", @"C:\src", []));
        Assert.Equal("claude --resume abc", Compose(ShellKind.Bash, "claude", "abc", "", []));
        Assert.Equal("devin --resume holy-antler --permission-mode dangerous",
            Compose(ShellKind.PowerShell, "devin", "holy-antler", null, new[] { "--permission-mode", "dangerous" }));
    }

    private static readonly string[] Cwds =
    {
        "", @"C:\src\proj", @"C:\Program Files\my proj", @"C:\src\it's",
        @"C:\x; claude --resume other", @"C:\x && codex resume other",   // the directory is the agent's text: it can say anything
    };

    [Theory]
    [InlineData(ShellKind.PowerShell)]
    [InlineData(ShellKind.Bash)]
    [InlineData(ShellKind.Cmd)]
    [InlineData(ShellKind.Other)]   // wsl and anything custom: the resume alone
    public void ResumesSessionMatchesEveryLineComposeEmits(ShellKind shell)
    {
        foreach (string agent in Agents)
        {
            string id = agent == "devin" ? "holy-antler" : "01a0ce4b-7125-7dd1-b5a1-a99d8f14402c";
            string running = agent switch
            {
                "codex" => CodexExe + " --sandbox read-only --dangerously-bypass-approvals-and-sandbox",
                "devin" => DevinExe + " --permission-mode dangerous --sandbox",
                _ => ClaudeExe + " --permission-mode plan --dangerously-skip-permissions",
            };
            foreach (var flags in new[] { Array.Empty<string>(), ResumeFlags(agent, running).ToArray() })
                foreach (string cwd in Cwds)
                {
                    string line = Compose(shell, agent, id, cwd, flags);
                    Assert.True(ResumesSession(line, agent, id), line);
                    Assert.False(ResumesSession(line, agent, id + "d"), line);        // a whole token, not a prefix
                    Assert.False(ResumesSession(line, agent, id[..^1]), line);
                    Assert.False(ResumesSession(line, agent, "other"), line);
                    Assert.False(ResumesSession(line, Agents.First(a => a != agent), id), line);   // another agent's session
                }
        }
    }

    [Theory]
    [InlineData("claude --resume abcd", "abc")]
    [InlineData("claude --resume abc", "abcd")]
    [InlineData("claude --resume abc-def --dangerously-skip-permissions", "abc")]
    [InlineData("claude --resume ABC", "abc")]
    [InlineData("codex resume 01a0 -s workspace-write", "01a")]
    public void ResumesSessionComparesWholeTokens(string line, string id)
        => Assert.False(ResumesSession(line, line.StartsWith("codex", StringComparison.Ordinal) ? "codex" : "claude", id));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("claude")]                                    // the launcher wrapper's legacy bindings
    [InlineData("claude --dangerously-skip-permissions")]
    [InlineData("claude --resume")]
    [InlineData("codex resume")]
    [InlineData("echo claude --resume abc")]                  // pinned text that merely mentions a session
    [InlineData("npm test; claude --resume abc \"fix it\"")]
    [InlineData("claude --resume abc && rm -rf x")]
    [InlineData("claude -r abc")]
    public void ResumesSessionNeverMatchesALineThatNamesNoSession(string? line)
        => Assert.False(ResumesSession(line, "claude", "abc"));

    [Theory]
    [InlineData("gemini", "abc")]
    [InlineData("claude", "a b")]
    [InlineData("claude", "")]
    public void ResumesSessionRefusesWhatCouldNotHaveBeenBound(string agent, string id)
        => Assert.False(ResumesSession($"{agent} --resume {id}", agent, id));

    [Fact]
    public void ResumesSessionDoesNotLookAtWhatStandsBeforeTheSeparator()
    {
        // Pinned text that ends in a session's resume run is taken for that session's binding: the prefix is
        // the agent's own directory in every line agwinterm writes, so it is not inspected.
        Assert.True(ResumesSession("npm test; claude --resume abc", "claude", "abc"));
        Assert.True(ResumesSession("make && claude --resume abc --dangerously-skip-permissions", "claude", "abc"));
    }

    [Theory]
    [InlineData(false, false, BindLanding.Store)]
    [InlineData(false, true, BindLanding.Unbind)]    // ended while resolving, and the walk accepted it: do not store
    [InlineData(true, false, BindLanding.Superseded)]
    [InlineData(true, true, BindLanding.Superseded)]  // a later bind owns the pane now; the older session's end is not its
    public void ABindingLandsByWhetherALaterOneWonAndWhetherItsSessionEnded(bool superseded, bool ended, BindLanding expected)
        => Assert.Equal(expected, LandBind(superseded, ended));

    [Theory]
    [InlineData("claude --resume abc", "abc")]
    [InlineData("cd '/c/x y' && claude --resume abc --dangerously-skip-permissions", "abc")]
    [InlineData("Set-Location -LiteralPath 'C:\\a b'; claude --resume 36145f11-e132 --permission-mode plan", "36145f11-e132")]
    [InlineData("claude", null)]
    [InlineData("claude --dangerously-skip-permissions", null)]
    [InlineData("echo claude --resume abc", null)]
    [InlineData(null, null)]
    public void ResumedSessionNamesTheIdTheLineResumes(string? line, string? expected)
        => Assert.Equal(expected, ResumedSession(line, "claude"));

    [Theory]
    [InlineData(@"""C:\Users\x y\.local\bin\claude.exe"" --resume abc", true)]            // a quoted path with spaces
    [InlineData(@"C:\Users\u\.local\bin\claude.exe --dangerously-skip-permissions", true)]
    [InlineData(@"""C:\Program Files\nodejs\node.exe"" C:\Users\u\AppData\Roaming\npm\node_modules\@anthropic-ai\claude-code\cli.js --resume abc", true)]   // npm
    [InlineData("claude", true)]
    [InlineData("claude --resume abc", true)]
    [InlineData("CLAUDE.EXE", true)]
    [InlineData("npm test", false)]
    [InlineData("claude-helper --serve", false)]
    [InlineData(@"""C:\Program Files\Git\bin\git.exe"" status", false)]
    [InlineData(@"""C:\Program Files\nodejs\node.exe"" server.js", false)]
    [InlineData(@"""C:\Users\u\bin\codex.exe"" resume abc", false)]
    [InlineData("python claude.py", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void ACapturedCommandIsClaudeByTheBindingsOwnRecognition(string? commandLine, bool expected)
        => Assert.Equal(expected, IsClaudeCommand(commandLine));

    [Fact]
    public void ResumesSessionTakesTheLegacyLinesThatNameTheSession()
    {
        // What RestartClaudeYolo and the adopt verb stored.
        const string id = "36145f11-e132-4691-ab98-ae5c4445c46b";
        Assert.True(ResumesSession($"claude --resume {id} --dangerously-skip-permissions", "claude", id));
        Assert.True(ResumesSession($"claude --resume {id}", "claude", id));
    }

    [Theory]
    [InlineData("\u2018")]
    [InlineData("\u2019")]
    [InlineData("\u201A")]
    [InlineData("\u201B")]
    public void PowerShellDoublesEveryCharacterThatEndsASingleQuotedString(string quote)
    {
        string cwd = $@"C:\src\x{quote}; Start-Process calc; {quote}";
        Assert.Equal($@"Set-Location -LiteralPath 'C:\src\x{quote}{quote}; Start-Process calc; {quote}{quote}'; claude --resume abc",
            Compose(ShellKind.PowerShell, "claude", "abc", cwd, []));
        // bash ends a single-quoted string only at the apostrophe
        Assert.Equal($"cd '{cwd}' && claude --resume abc", Compose(ShellKind.Bash, "claude", "abc", cwd, []));
    }

    [Fact]
    public void PowerShellQuotedLeavesOtherCharactersAlone()
        => Assert.Equal("a''b\u201C$x`;c", PowerShellQuoted("a'b\u201C$x`;c"));

    [Theory]
    [InlineData("C:\\a\"b")]
    [InlineData(@"C:\work\%OS%")]
    public void CmdLeavesOutADirectoryItsQuotesCannotHold(string cwd)
        => Assert.Equal("codex resume 01a0", Compose(ShellKind.Cmd, "codex", "01a0", cwd, []));

    [Theory]
    [InlineData(ShellKind.PowerShell)]
    [InlineData(ShellKind.Bash)]
    [InlineData(ShellKind.Cmd)]
    public void ADirectoryWithAControlCharacterIsNeverTyped(ShellKind shell)
        => Assert.Equal("claude --resume abc", Compose(shell, "claude", "abc", "C:\\src\nStart-Process calc", []));

    [Theory]
    [InlineData(@"C:\Program Files\Git\bin\bash.exe", ShellKind.Bash)]
    [InlineData("powershell.exe", ShellKind.PowerShell)]
    [InlineData(@"C:\Program Files\PowerShell\7\pwsh.exe", ShellKind.PowerShell)]
    [InlineData("cmd.exe", ShellKind.Cmd)]
    [InlineData("wsl.exe", ShellKind.Other)]
    [InlineData(null, ShellKind.Other)]
    public void ShellsAreClassifiedByTheirExecutable(string? exe, ShellKind expected) => Assert.Equal(expected, ClassifyShell(exe));

    [Theory]
    [InlineData("01a0ce4b-7125-7dd1-b5a1-a99d8f14402c", true)]
    [InlineData("thr_123", true)]
    [InlineData("holy-antler", true)]
    [InlineData("", false)]
    [InlineData("a b", false)]
    [InlineData("x;rm", false)]
    [InlineData(null, false)]
    public void SessionIdsAreSafeToTypeUnquoted(string? id, bool valid) => Assert.Equal(valid, IsValidSessionId(id));

    [Fact]
    public void SplitCommandLineFollowsCommandLineToArgvW()
    {
        Assert.Equal(new[] { @"C:\Program Files\nodejs\node.exe", "a b", "c\"d", @"e\\f", @"g\" },
            SplitCommandLine(@"""C:\Program Files\nodejs\node.exe"" ""a b"" c\""d e\\f ""g\\"""));
        Assert.Equal(new[] { "x", "" }, SplitCommandLine("x \"\""));
    }
}
