# Mirrored in agliteterm/test. Run only inside each product's owned-process supervisor.
# Adapter: CommandRpc verb args target [AllowError], $commandArtifact and $commandCtl.
$commandChecks=0;$commandIds=@()
function CommandCheck([bool]$ok,[string]$name){if(-not $ok){throw $name};$script:commandChecks++;"PASS $name"}
function CommandText([string]$id){[string](CommandRpc 'session.text' @{all=$true} $id)}
function CommandWait([string]$id,[string]$text){
    for($try=0;$try-lt 100;$try++){
        try{if((CommandText $id).Contains($text)){return $true}}catch{}
        Start-Sleep -Milliseconds 100
    }
    (CommandText $id)|Set-Content -LiteralPath (Join-Path $commandArtifact ('missing-'+$id.Replace(':','_')+'.txt'))
    return $false
}
function CommandNew($args_){
    $args_['no-select']=$true;$args_['cwd']=$commandArtifact
    $id=[string](CommandRpc 'session.new' $args_ '')
    $script:commandIds+=,$id;return $id
}
try{
    $file=Join-Path $commandArtifact 'remove me.txt';'owned fixture'|Set-Content -LiteralPath $file
    $command="Remove-Item -LiteralPath './remove me.txt'; Write-Output ('CMD-'+'READY'); Write-Output ('CWD='+ (Get-Location).Path); Write-Output ('PANE='+`$env:AGWINTERM_SESSION_ID); Write-Output 'quoted `"value`" ☃'"
    $id=CommandNew @{command=$command;name='command-default'}
    CommandCheck (CommandWait $id 'CMD-READY') 'default executes PowerShell cmdlet and statements'
    CommandCheck (-not (Test-Path -LiteralPath $file)) 'cmdlet uses the requested working directory'
    CommandCheck (CommandWait $id ('PANE='+$id)) 'command inherits its own pane identity'
    CommandCheck (CommandWait $id 'quoted "value" ☃') 'PowerShell preserves quotes and Unicode'
    $null=CommandRpc 'session.type' @{text="Write-Output ('AFTER-'+'READY')`r"} $id
    CommandCheck (CommandWait $id 'AFTER-READY') 'default leaves a live interactive prompt'

    $held=CommandNew @{command="Write-Output ('WAIT-'+'READY')";wait=$true;name='command-wait'}
    CommandCheck (CommandWait $held 'WAIT-READY') 'wait uses the same PowerShell interpreter'
    $null=CommandRpc 'session.type' @{text="Write-Output ('WAIT-AFTER-'+'READY')`r"} $held
    CommandCheck (CommandWait $held 'WAIT-AFTER-READY') 'wait leaves the interactive prompt available'

    # Explicit helper executable with a spaced relative script path, for portable workbench.cmd.
    $helper=Join-Path $commandArtifact 'pane helper.ps1'
    "param([string]`$Value)`nWrite-Output ('HELPER='+`$Value)"|Set-Content -LiteralPath $helper
    $scriptPane=CommandNew @{command="powershell.exe -NoLogo -NoProfile -File './pane helper.ps1' -Value 'a b'";name='command-helper'}
    CommandCheck (CommandWait $scriptPane 'HELPER=a b') 'explicit PowerShell helper preserves spaced path and argument'

    $toolDir=Join-Path $commandArtifact 'tool folder';New-Item -ItemType Directory $toolDir|Out-Null
    $tool=Join-Path $toolDir 'cmd.exe';Copy-Item -LiteralPath "$env:WINDIR/System32/cmd.exe" -Destination $tool
    $spaced=CommandNew @{command=('"'+$tool+'" /d /c echo SPACE-READY');'command-mode'='direct';name='command-space'}
    CommandCheck (CommandWait $spaced 'SPACE-READY') 'direct executable path can contain spaces'

    # Report argv from an actual executable, so neither PowerShell nor cmd can repair it.
    $argvSource=Join-Path $commandArtifact 'argv helper.cs'
    $argvExe=Join-Path $commandArtifact 'argv helper.exe'
    @'
using System;
using System.Text;
class ArgvHelper {
    static void Main(string[] args) {
        Console.WriteLine("ARGC=" + args.Length);
        for (int i = 0; i < args.Length; i++)
            Console.WriteLine("ARG" + i + "=" + Convert.ToBase64String(Encoding.UTF8.GetBytes(args[i])) + ":END");
    }
}
'@ | Set-Content -LiteralPath $argvSource
    & "$env:WINDIR/Microsoft.NET/Framework64/v4.0.30319/csc.exe" /nologo /target:exe "/out:$argvExe" $argvSource
    if($LASTEXITCODE-ne 0){throw 'argv helper compilation failed'}
    $argvPane=CommandNew @{command=('"'+$argvExe+'" "" "a b" "say \"hi\"" "C:\tail\\" "☃"');'command-mode'='direct';name='command-argv'}
    CommandCheck (CommandWait $argvPane 'ARGC=5') 'direct host preserves argument count'
    $expectedArgs=@('', 'a b', 'say "hi"', 'C:\tail\', '☃')
    for($argIndex=0;$argIndex-lt $expectedArgs.Count;$argIndex++){
        $encodedArg=[Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($expectedArgs[$argIndex]))
        CommandCheck (CommandWait $argvPane ("ARG${argIndex}="+$encodedArg+':END')) "direct host preserves exact argument $argIndex"
    }

    $failedPane=CommandNew @{command='agwinterm-command-fixture-missing.exe';'command-mode'='direct';name='command-failure';'workspace-name'='command-failure-workspace';'create-workspace'=$true}
    CommandCheck (CommandWait $failedPane 'start') 'failed executable retains a diagnostic pane'
    CommandCheck (-not (CommandRpc 'session.type' @{text='MUST-NOT-RUN'} $failedPane -AllowError).ok) 'failed executable has no interpreter accepting input'
    $failedTree=CommandRpc 'tree' @{} ''
    CommandCheck (@($failedTree.workspaces|Where-Object { $_.name-eq 'command-failure-workspace' -and $_.sessions.id -contains $failedPane }).Count-eq 1) 'failed executable belongs to the requested published workspace'

    $sink="[Console]::WriteLine('DIRECT-READY');`$s=[Console]::ReadLine();[Console]::WriteLine('LITERAL='+`$s);exit 7"
    $encoded=[Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($sink))
    $direct=CommandNew @{command=('powershell.exe -NoProfile -EncodedCommand '+$encoded);'command-mode'='direct';name='command-direct'}
    CommandCheck (CommandWait $direct 'DIRECT-READY') 'direct mode starts the requested program'
    $literal='$x; Write-Output SHOULD-NOT-RUN'
    $null=CommandRpc 'session.type' @{text=($literal+"`r")} $direct
    CommandCheck (CommandWait $direct ('LITERAL='+$literal)) 'direct program receives literal shell metacharacters'
    $dead=$false
    for($try=0;$try-lt 80;$try++){
        $answer=CommandRpc 'session.type' @{text=' '} $direct -AllowError
        if(-not $answer.ok){$dead=$true;break};Start-Sleep -Milliseconds 100
    }
    CommandCheck $dead 'direct program exit leaves no interactive shell accepting writes'
    CommandCheck ((CommandText $direct).Contains('LITERAL='+$literal)) 'exited standalone pane retains output'

    $exitPane=CommandNew @{command="Write-Output ('EXIT-'+'READY');exit 7";name='command-exit'}
    CommandCheck (CommandWait $exitPane 'EXIT-READY') 'explicit PowerShell exit retains preceding output'
    $dead=$false
    for($try=0;$try-lt 80;$try++){
        $answer=CommandRpc 'session.type' @{text=' '} $exitPane -AllowError
        if(-not $answer.ok){$dead=$true;break};Start-Sleep -Milliseconds 100
    }
    CommandCheck $dead 'explicit PowerShell exit ends the interpreter'

    foreach($args_ in @(
        @{command='echo ok';'command-mode'='invalid'},
        @{command='echo ok';'command-mode'=$null},
        @{command='echo ok';'command-mode'=@{}},
        @{command='echo ok';'command-mode'=@()},
        @{command='echo ok';'command-mode'='direct';wait=$true},
        @{'command-mode'='direct'},
        @{command='echo ok';profile='missing-profile'},
        @{command=('x'*2048)},
        @{command='"" arg';'command-mode'='direct'}
    )){
        $before=(CommandRpc 'tree' @{} '')|ConvertTo-Json -Compress -Depth 20
        $args_['workspace-name']='must-not-create';$args_['create-workspace']=$true
        $answer=CommandRpc 'session.new' $args_ '' -AllowError
        CommandCheck (-not $answer.ok) 'malformed launch is refused'
        $after=(CommandRpc 'tree' @{} '')|ConvertTo-Json -Compress -Depth 20
        # Compare identities; asynchronous status/foreground metadata can change independently.
        $beforeIds=@(($before|ConvertFrom-Json).workspaces|ForEach-Object { $_.id; $_.sessions.id })-join ','
        $afterIds=@(($after|ConvertFrom-Json).workspaces|ForEach-Object { $_.id; $_.sessions.id })-join ','
        CommandCheck ($beforeIds-ceq $afterIds) 'refusal creates neither workspace nor session'
    }
    foreach($bareFlag in '--command','--command-mode'){
        $before=CommandRpc 'tree' @{} ''
        $null=& $commandCtl session new $bareFlag --pipe $commandPipe --no-select --json 2>&1
        CommandCheck ($LASTEXITCODE-eq 2) "CLI refuses bare $bareFlag"
        $after=CommandRpc 'tree' @{} ''
        $beforeIds=@($before.workspaces|ForEach-Object { $_.id; $_.sessions.id })-join ','
        $afterIds=@($after.workspaces|ForEach-Object { $_.id; $_.sessions.id })-join ','
        CommandCheck ($beforeIds-ceq $afterIds) 'bare CLI flag creates no workspace or session'
    }
    # Exercise the shared CLI flag, not only handcrafted JSON. The adapter owns its pipe.
    $json=& $commandCtl session new --command-mode direct --command 'cmd.exe /d /c echo CLI-DIRECT-READY' --pipe $commandPipe --no-select --json
    CommandCheck ($LASTEXITCODE-eq 0) 'shared CLI sends command-mode'
    $cliId=[string]($json|ConvertFrom-Json).result;$commandIds+=,$cliId
    CommandCheck (CommandWait $cliId 'CLI-DIRECT-READY') 'CLI direct launch executes in the terminal'
    "Session command acceptance: $commandChecks checks passed"
}finally{
    foreach($id in $commandIds){$null=CommandRpc 'session.close' @{} $id -AllowError}
}
