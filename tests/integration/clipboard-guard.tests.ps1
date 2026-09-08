# The clipboard guard against a fake clipboard: no window station, no real clipboard, nothing to
# restore. Every state in clipboard-guard.ps1's header has a case here, and so has every way the
# real one was wrong in review (#256 round 5): a user copy between the snapshot and the sentinel,
# a user copy after the sentinel, a partial put on the restore, a sentinel write that fails after
# the empty; and round 6's two: a restore refused five times after a successful write, and a datum
# that could not be read taken for someone else's copy. Runs in CI before win32-control.ps1.
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'clipboard-guard.ps1')
$fail = 0
function Check([string]$name, [bool]$ok, [string]$detail = '') {
    if ($ok) { "  PASS  $name" }
    else { $script:fail++; "  FAIL  $name$(if ($detail) { " — $detail" })" }
}
$guard = [Agwinterm.Win32ControlTest.ClipboardGuard]
$U = [Text.Encoding]::Unicode
function New-Fake {
    $fake = [Agwinterm.Win32ControlTest.FakeClipboardApi]::new()
    # A text clipboard as Windows synthesizes it: CF_UNICODETEXT with CF_LOCALE, CF_TEXT, CF_OEMTEXT.
    $fake.Store[13] = $U.GetBytes("hello`0"); $fake.Store[16] = [byte[]](9, 4, 0, 0)
    $fake.Store[1] = [Text.Encoding]::ASCII.GetBytes("hello`0"); $fake.Store[7] = [Text.Encoding]::ASCII.GetBytes("hello`0")
    $guard::Api = $fake
    $fake
}
function Same($fake, $snap) { $snap.SameAs((& { $guard::Api = $fake; $guard::Take() })) }

"== clipboard guard (fake) =="
try {
    # The happy path: take, sentinel, restore, proven.
    $f = New-Fake
    $snap = $guard::Take()
    Check 'Take() carries every format and the sequence it was read under' ($snap.Formats.Count -eq 4 -and $snap.Sequence -eq $f.Seq -and -not $snap.Unsupported) "names=$($snap.Names) seq=$($snap.Sequence)/$($f.Seq)"
    $w = $guard::WriteSentinel('agw-paste-1', $snap)
    Check 'WriteSentinel() → written; the clipboard holds the sentinel with the three formats the close synthesized; Sequence is the number AFTER the close (three past the writer''s open), read under a second open' ($w.State -eq 'written' -and $f.Store.Count -eq 4 -and $U.GetString($f.Store[13]) -eq "agw-paste-1`0" -and $w.Sequence -eq $f.Seq -and $w.Sentinel -eq 'agw-paste-1' -and $w.Detail -like 'generation * (the close moved the sequence * -> *; the sentinel is in)' -and $f.Opens -eq 3 -and -not $f.IsOpen) "$w store=$($f.Names) seq=$($f.Seq)"
    $r = Invoke-ClipboardRestore $snap $w $null
    Check 'Restore() → restored, and the store holds the snapshot byte for byte' ($r.State -eq 'restored' -and (Same $f $snap)) "$r store=$($f.Names)"
    Check '(the number did not move at that close: every synthesizable format was put explicitly)' ($r.Sequence -eq $f.Seq) "restored-at=$($r.Sequence) now=$($f.Seq)"

    # The second open refused: the generation stays the writer's number, said so; the restore finds the
    # number moved and proves ownership by content — exactly the sentinel — before it writes.
    $f = New-Fake
    $snap = $guard::Take()
    $f.FailOpensAfter = 2
    $w = $guard::WriteSentinel('agw-paste-1b', $snap)
    $f.FailOpensAfter = 0
    $r = Invoke-ClipboardRestore $snap $w $null
    Check 'the second open refused → unverified (the case does not run; the writer''s number, three behind); Restore() proves the sentinel by content → restored' ($w.State -eq 'unverified' -and $w.Detail -like '*could not be reopened*' -and $w.Sequence -eq $r.Sequence - 3 - 5 -and $r.State -eq 'restored' -and (Same $f $snap)) "w=$w r=$r"
    $f = New-Fake
    $snap = $guard::Take()
    $f.FailOpensAfter = 2
    $w = $guard::WriteSentinel('agw-paste-1c', $snap)
    $f.FailOpensAfter = 0
    $f.UserWrites(13, $U.GetBytes("agw-paste-1c`0")); $f.Store[0xC0F3] = [Text.Encoding]::ASCII.GetBytes('theirs')
    $r = Invoke-ClipboardRestore $snap $w $null
    Check 'a copy carrying the sentinel''s text beside a format the system does not synthesize is not the sentinel → changed' ($r.State -eq 'changed' -and $f.Store.ContainsKey([uint32]0xC0F3)) "$r store=$($f.Names)"

    # A user copy BETWEEN the snapshot and the sentinel: nothing is written over it.
    $f = New-Fake
    $snap = $guard::Take()
    $f.UserWrites(13, $U.GetBytes("theirs`0"))
    $w = $guard::WriteSentinel('agw-paste-2', $snap)
    Check 'a user copy between Take() and WriteSentinel() → changed, their copy untouched' ($w.State -eq 'changed' -and $f.Empties -eq 0 -and $f.Sets -eq 0 -and $U.GetString($f.Store[13]) -eq "theirs`0") "$w empties=$($f.Empties) sets=$($f.Sets)"

    # A user copy AFTER the sentinel: the restore keeps theirs.
    $f = New-Fake
    $snap = $guard::Take()
    $w = $guard::WriteSentinel('agw-paste-3', $snap)
    $f.UserWrites(13, $U.GetBytes("theirs`0"))
    $sets = $f.Sets
    $r = Invoke-ClipboardRestore $snap $w $null
    Check 'a user copy after the sentinel → Restore() changed, theirs kept' ($r.State -eq 'changed' -and $f.Sets -eq $sets -and $U.GetString($f.Store[13]) -eq "theirs`0") "$r store=$($f.Names)"

    # A partial put on the restore: one Set fails after the empty. The retry under the SAME open
    # restores; the sequence the clipboard shows afterwards is the guard's own, never read as a user's.
    $f = New-Fake
    $snap = $guard::Take()
    $w = $guard::WriteSentinel('agw-paste-4', $snap)
    $f.SetFailures = 1
    $r = Invoke-ClipboardRestore $snap $w $null
    Check 'one Set fails after the empty → retried under the same open → restored, proven' ($r.State -eq 'restored' -and $f.Opens -eq 4 -and (Same $f $snap)) "$r opens=$($f.Opens) store=$($f.Names)"

    # Every Set fails: the clipboard was emptied by US. The word is mutated — never changed — and the
    # loop does not retry with the old sequence; the file is kept.
    $f = New-Fake
    $snap = $guard::Take()
    $w = $guard::WriteSentinel('agw-paste-5', $snap)
    $file = Join-Path ([IO.Path]::GetTempPath()) ("agwinterm-clipboard-test-" + [guid]::NewGuid().ToString('N') + '.bin')
    $snap.Save($file)
    $f.SetFailures = 100
    $opens = $f.Opens
    $r = Invoke-ClipboardRestore $snap $w $file
    Check 'every Set fails after the empty → mutated (not changed), one transaction, the file kept' ($r.State -eq 'mutated' -and $f.Opens -eq $opens + 1 -and (Test-Path -LiteralPath $file)) "$r opens=$($f.Opens - $opens)"
    $f2 = New-Fake
    $snap2 = $guard::Take(); $w2 = $guard::WriteSentinel('agw-paste-5b', $snap2)
    $file2 = Join-Path ([IO.Path]::GetTempPath()) ("agwinterm-clipboard-test-" + [guid]::NewGuid().ToString('N') + '.bin')
    $snap2.Save($file2)
    $f2.UserWrites(13, $U.GetBytes("theirs`0"))
    $r2 = Invoke-ClipboardRestore $snap2 $w2 $file2
    Check 'the file is deleted on changed (theirs replaced it as any copy would) and on restored; kept only on mutated' ($r2.State -eq 'changed' -and -not (Test-Path -LiteralPath $file2)) "$r2"
    $guard::Api = $f
    $f.SetFailures = 0
    $again = $guard::Restore($snap, $w)
    Check '(a retry with the old write WOULD say changed — the reason the loop never makes it: the number moved and the sentinel is gone)' ($again.State -eq 'changed') "$again"

    # Recovery: the file goes back whatever the clipboard holds, proven.
    $fr = Restore-ClipboardFile $file
    Check 'Restore-ClipboardFile → restored from the DPAPI file, the store holds the snapshot, the file deleted' ($fr.State -eq 'restored' -and (Same $f $snap) -and -not (Test-Path -LiteralPath $file)) "$fr"

    # The sentinel write fails after the empty; the snapshot goes back, proven: put back.
    $f = New-Fake
    $snap = $guard::Take()
    $f.SetFailures = 1
    $w = $guard::WriteSentinel('agw-paste-6', $snap)
    Check 'the sentinel Set fails after the empty → put back, the snapshot proven in the store' ($w.State -eq 'put back' -and (Same $f $snap)) "$w store=$($f.Names)"

    # The sentinel write fails and so does the put-back: mutated.
    $f = New-Fake
    $snap = $guard::Take()
    $f.SetFailures = 100
    $w = $guard::WriteSentinel('agw-paste-7', $snap)
    Check 'the sentinel Set and the put-back both fail → mutated' ($w.State -eq 'mutated' -and $f.Store.Count -eq 0) "$w store=$($f.Names)"

    # Empty refused: nothing touched → failed.
    $f = New-Fake
    $snap = $guard::Take()
    $f.EmptyFails = $true
    $w = $guard::WriteSentinel('agw-paste-8', $snap)
    Check 'EmptyClipboard refused before the sentinel → failed, nothing touched' ($w.State -eq 'failed' -and $f.Sets -eq 0 -and (Same $f $snap)) "$w"
    $f.EmptyFails = $false
    $w = $guard::WriteSentinel('agw-paste-8', $snap)
    $f.EmptyFails = $true
    $r = Invoke-ClipboardRestore $snap $w $null
    Check 'EmptyClipboard refused on the restore (the sentinel stays) → mutated' ($r.State -eq 'mutated' -and $U.GetString($f.Store[13]) -eq "agw-paste-8`0") "$r"
    # The put-back succeeds but its read-back cannot read every format: mutated (the clipboard is in
    # an unknown state), and the Detail names what was READ before what was not — the same order as
    # every other Detail (round 9 of #256: the put-back proof printed the unreadable format alone).
    $f = New-Fake
    $snap = $guard::Take()
    $w = $guard::WriteSentinel('agw-paste-8b', $snap)
    $f.GetFails = 16
    $r = Invoke-ClipboardRestore $snap $w $null
    Check 'the put-back read-back cannot read CF_LOCALE → mutated, its Detail names the text READ before the unreadable format' ($r.State -eq 'mutated' -and $r.Detail -like 'the read-back holds 13=CF_UNICODETEXT`[*`], then format 16 (CF_LOCALE), whose data could not be read*') "$r"
    $f.GetFails = 0

    # Cannot open: unopened, retried by the loop, nothing touched.
    $f = New-Fake
    $snap = $guard::Take()
    $f.OpenFails = $true
    $w = $guard::WriteSentinel('agw-paste-9', $snap)
    $r = Invoke-ClipboardRestore $snap $w $null
    Check 'the clipboard cannot be opened → unopened from both, nothing touched' ($w.State -eq 'unopened' -and $r.State -eq 'unopened' -and $f.Sets -eq 0 -and $f.Empties -eq 0) "w=$w r=$r"
    $threw = $false
    try { $guard::Take() | Out-Null } catch { $threw = $true }
    Check 'Take() throws when the clipboard cannot be opened' $threw

    # Round 6, Major 1: written, then the clipboard cannot be reopened for the restore — `unopened` five
    # times over. The sentinel was on the clipboard when the write closed and nothing was put back, so the
    # file is KEPT (it was deleted, and the
    # case passed over a clipboard still holding the sentinel).
    $f = New-Fake
    $snap = $guard::Take()
    $w = $guard::WriteSentinel('agw-paste-9b', $snap)
    $file = Join-Path ([IO.Path]::GetTempPath()) ("agwinterm-clipboard-test-" + [guid]::NewGuid().ToString('N') + '.bin')
    $snap.Save($file)
    $f.OpenFails = $true
    $sets = $f.Sets
    $r = Invoke-ClipboardRestore $snap $w $file
    Check 'written, then the restore cannot open five times → unopened, the sentinel still in, the file KEPT' ($w.State -eq 'written' -and $r.State -eq 'unopened' -and $r.Detail -like '*5 tries*' -and $f.Sets -eq $sets -and $U.GetString($f.Store[13]) -eq "agw-paste-9b`0" -and (Test-Path -LiteralPath $file)) "w=$w r=$r"
    $f.OpenFails = $false
    $r = Invoke-ClipboardRestore $snap $w $file
    Check 'the same write restores once the clipboard opens again; the file deleted then' ($r.State -eq 'restored' -and (Same $f $snap) -and -not (Test-Path -LiteralPath $file)) "$r"
    Remove-Item -LiteralPath $file -Force -ErrorAction SilentlyContinue

    # Round 6, Major 2: the sequence moved without a copy and the sentinel's data cannot be READ — that
    # is not someone else's copy (`changed` deleted the file and the case passed): `unread`, nothing
    # touched, the file kept; and the same under Generation's second open leaves the generation
    # unverified rather than skipping the case with the sentinel in.
    $f = New-Fake
    $snap = $guard::Take()
    $w = $guard::WriteSentinel('agw-paste-10', $snap)
    $file = Join-Path ([IO.Path]::GetTempPath()) ("agwinterm-clipboard-test-" + [guid]::NewGuid().ToString('N') + '.bin')
    $snap.Save($file)
    $f.Seq++; $f.GetFails = 13
    $sets = $f.Sets; $empties = $f.Empties
    $r = Invoke-ClipboardRestore $snap $w $file
    Check 'the number moved and CF_UNICODETEXT cannot be read → unread (not changed), nothing touched, the file kept' ($w.State -eq 'written' -and $r.State -eq 'unread' -and $r.Detail -like '*could not be read*' -and $f.Sets -eq $sets -and $f.Empties -eq $empties -and $U.GetString($f.Store[13]) -eq "agw-paste-10`0" -and (Test-Path -LiteralPath $file)) "w=$w r=$r"
    $f.GetFails = 16
    $r = Invoke-ClipboardRestore $snap $w $file
    Check 'a synthesized format that cannot be read → unread too' ($r.State -eq 'unread' -and (Test-Path -LiteralPath $file)) "$r"
    $f.GetFails = 0
    # Round 7: a failed enumeration is not an empty clipboard (which is positively theirs → changed).
    $f.FormatsFail = $true
    $r = Invoke-ClipboardRestore $snap $w $file
    Check 'the formats cannot be enumerated → unread (not changed: an empty clipboard is), nothing touched, the file kept' ($r.State -eq 'unread' -and $r.Detail -like '*could not be enumerated*' -and $f.Sets -eq $sets -and $f.Empties -eq $empties -and (Test-Path -LiteralPath $file)) "$r"
    $f.FormatsFail = $false
    $r = Invoke-ClipboardRestore $snap $w $file
    Check 'readable again: the same write restores by content; the file deleted then' ($r.State -eq 'restored' -and (Same $f $snap) -and -not (Test-Path -LiteralPath $file)) "$r"
    Remove-Item -LiteralPath $file -Force -ErrorAction SilentlyContinue
    $f = New-Fake
    $snap = $guard::Take()
    $f.GetFails = 13
    $w = $guard::WriteSentinel('agw-paste-10b', $snap)
    Check 'CF_UNICODETEXT unreadable under the second open → unverified (the case does not run), the inside number' ($w.State -eq 'unverified' -and $w.Detail -like '*could not be read*' -and $w.Sequence -eq $f.Seq - 3) "$w seq=$($f.Seq)"
    $f.GetFails = 0
    $r = Invoke-ClipboardRestore $snap $w $null
    Check 'then restored by content' ($r.State -eq 'restored' -and (Same $f $snap)) "$r"
    $f = New-Fake
    $snap = $guard::Take()
    $f.FormatsFail = $true
    $w = $guard::WriteSentinel('agw-paste-10d', $snap)
    Check 'the formats cannot be enumerated under the second open → unverified (the case does not run), the inside number' ($w.State -eq 'unverified' -and $w.Detail -like '*could not be enumerated*' -and $w.Sequence -eq $f.Seq - 3) "$w seq=$($f.Seq)"
    $f.FormatsFail = $false
    $r = Invoke-ClipboardRestore $snap $w $null
    Check 'then restored by content' ($r.State -eq 'restored' -and (Same $f $snap)) "$r"
    $f = New-Fake
    $f.FormatsFail = $true
    $snap = $guard::Take()
    Check 'Take() on a clipboard whose formats cannot be enumerated → Unsupported (the case does not run), not an empty snapshot' ($snap.Unsupported -like '*could not be enumerated*') "unsupported=$($snap.Unsupported) names=$($snap.Names)"
    $f.FormatsFail = $false
    # Round 7 (Codex): differing CF_UNICODETEXT read BEFORE a later format fails → the difference is
    # positive evidence: `changed` at the second open (no paste), `changed` at the restore (theirs kept).
    $f = New-Fake
    $snap = $guard::Take()
    $f.FailOpensAfter = 2
    $w = $guard::WriteSentinel('agw-paste-10e', $snap)
    $f.FailOpensAfter = 0
    $f.UserWrites(13, $U.GetBytes("theirs`0")); $f.Store[16] = [byte[]](0, 0, 0, 0); $f.GetFails = 16
    $r = Invoke-ClipboardRestore $snap $w $null
    Check 'other text before an unreadable CF_LOCALE → changed at the restore (not unread), theirs kept' ($w.State -eq 'unverified' -and $r.State -eq 'changed' -and $U.GetString($f.Store[13]) -eq "theirs`0") "w=$w r=$r"
    Check '  and its Detail names what was READ (the differing text, by format) before what was not' ($r.Detail -like '*13=CF_UNICODETEXT`[*`], then format 16 (CF_LOCALE), whose data could not be read*') "$r"
    $f.GetFails = 0
    $f = New-Fake
    $snap = $guard::Take()
    $f.CopyAtSecondOpen = $U.GetBytes("theirs`0"); $f.GetFails = 16
    $w = $guard::WriteSentinel('agw-paste-10f', $snap)
    Check 'other text before an unreadable CF_LOCALE under the SECOND open → changed (no paste, not unverified)' ($w.State -eq 'changed' -and $U.GetString($f.Store[13]) -eq "theirs`0") "$w"
    Check '  and its Detail names what was READ before what was not' ($w.Detail -like '*13=CF_UNICODETEXT`[*`], then format 16 (CF_LOCALE), whose data could not be read*') "$w"
    $f.GetFails = 0
    # Positively theirs stays `changed`: a format the sentinel never carries beside an unreadable one, an
    # image (an Unsupported id), other text, an emptied clipboard.
    $f = New-Fake
    $snap = $guard::Take()
    $w = $guard::WriteSentinel('agw-paste-10c', $snap)
    $f.Seq++; $f.Store[0xC0F3] = [Text.Encoding]::ASCII.GetBytes('theirs'); $f.GetFails = 13
    $r = Invoke-ClipboardRestore $snap $w $null
    Check 'a foreign format beside an unreadable CF_UNICODETEXT is positively theirs → changed' ($r.State -eq 'changed' -and $f.Store.ContainsKey([uint32]0xC0F3)) "$r"
    $f.GetFails = 0
    $f = New-Fake
    $snap = $guard::Take()
    $w = $guard::WriteSentinel('agw-paste-10d', $snap)
    $f.UserWrites(2, [byte[]](1, 2, 3))
    $r = Invoke-ClipboardRestore $snap $w $null
    Check 'an image copied during the case (CF_BITMAP) is positively theirs → changed, kept' ($r.State -eq 'changed' -and $r.Detail -like '*CF_BITMAP*' -and $f.Store.ContainsKey([uint32]2)) "$r"
    $f = New-Fake
    $snap = $guard::Take()
    $w = $guard::WriteSentinel('agw-paste-10e', $snap)
    $f.Store.Clear(); $f.Seq++
    $r = Invoke-ClipboardRestore $snap $w $null
    Check 'an emptied clipboard is positively not ours → changed, nothing written' ($r.State -eq 'changed' -and $f.Store.Count -eq 0) "$r"

    # A format that is not global memory: Unsupported, nothing written. An image clipboard as Windows
    # presents it (a DIB with its synthesized CF_BITMAP and CF_PALETTE) never runs.
    $f = New-Fake
    $f.Store[2] = [byte[]](1)
    $snap = $guard::Take()
    Check 'CF_BITMAP → Unsupported names it' ($snap.Unsupported -like 'format 2 (CF_BITMAP)*') "$($snap.Unsupported)"
    $f = New-Fake
    $f.Store[8] = [byte[]](40, 0, 0, 0); $f.Store[17] = [byte[]](124, 0, 0, 0); $f.Store[2] = [byte[]](1); $f.Store[9] = [byte[]](1)
    $snap = $guard::Take()
    Check 'CF_DIB + CF_DIBV5 + the synthesized CF_BITMAP and CF_PALETTE → Unsupported, nothing carried' ($snap.Unsupported -match '^format (2 \(CF_BITMAP\)|9 \(CF_PALETTE\))' -and $snap.Formats.Count -eq 0) "$($snap.Unsupported)"
    $f = New-Fake
    $f.Store[9] = [byte[]](1)
    Check 'CF_PALETTE → Unsupported' (($guard::Take()).Unsupported -like 'format 9 (CF_PALETTE)*')
    $f = New-Fake
    $f.Store[0x250] = [byte[]](1)
    Check 'the private range → Unsupported' (($guard::Take()).Unsupported -like 'format 592 (CF_PRIVATE)*')
    $f = New-Fake
    $f.Store[0x310] = [byte[]](1)
    Check 'the GDI-object range → Unsupported (global memory the system would not free)' (($guard::Take()).Unsupported -like 'format 784 (CF_GDIOBJ)*')
    $f = New-Fake
    $f.Store[15] = [byte[]](20, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 0, 0, 0, 67, 0, 58, 0, 92, 0, 0, 0, 0, 0)
    $snap = $guard::Take()
    Check 'CF_HDROP (global memory: a DROPFILES) is carried' ((-not $snap.Unsupported) -and ($snap.Formats -contains 15)) "names=$($snap.Names)"
    $f = New-Fake
    $f.Store.Remove([uint32]13) | Out-Null
    $f.Store[0xC0F3] = [Text.Encoding]::ASCII.GetBytes('Version:0.9')
    $snap = $guard::Take()
    Check 'a registered format (id >= 0xC000) is carried by its id' ((-not $snap.Unsupported) -and ($snap.Formats -contains 0xC0F3)) "names=$($snap.Names)"
    Check 'a stale ClipboardGuard type is refused: the source revision is the loaded one' (('Agwinterm.Win32ControlTest.ClipboardGuard' -as [type])::Revision -eq $clipboardGuardRevision) "loaded=$(('Agwinterm.Win32ControlTest.ClipboardGuard' -as [type])::Revision) source=$clipboardGuardRevision"

    # The DPAPI file: a round trip; the bytes on disk are not the clipboard's.
    $f = New-Fake
    $snap = $guard::Take()
    $file = Join-Path ([IO.Path]::GetTempPath()) ("agwinterm-clipboard-test-" + [guid]::NewGuid().ToString('N') + '.bin')
    try {
        $snap.Save($file)
        $disk = [IO.File]::ReadAllBytes($file)
        $plain = $U.GetBytes('hello')
        $found = $false
        for ($i = 0; $i -le $disk.Length - $plain.Length; $i++) { if ([Linq.Enumerable]::SequenceEqual([byte[]]$disk[$i..($i + $plain.Length - 1)], $plain)) { $found = $true; break } }
        $back = [Agwinterm.Win32ControlTest.ClipboardSnapshot]::Load($file)
        Check 'Save() then Load() round-trips every format; the file does not hold the text in the clear' ($snap.SameAs($back) -and -not $found) "names=$($back.Names) plaintext-found=$found"
        $threw = $false
        try { $snap.Save($file) } catch { $threw = $true }
        Check 'Save() refuses to overwrite an existing file' $threw
    } finally { Remove-Item -LiteralPath $file -Force -ErrorAction SilentlyContinue }
} finally {
    $guard::Api = [Agwinterm.Win32ControlTest.NativeClipboardApi]::new()
}

if ($fail) { "clipboard guard: $fail FAILED"; exit 1 }
"clipboard guard: all passed"
exit 0
