# Build agliteterm: MSVC cl.exe (located via vswhere), plus the Rust workspace
# it depends on. Output: lite\bin\ with the exe + agwinterm_core.dll + agwinterm-ptyhost.exe.
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent

# 1) The Rust server + core the client rides on.
cargo build --release --manifest-path (Join-Path $root 'native\Cargo.toml')
if ($LASTEXITCODE -ne 0) { throw 'cargo build failed' }

# 2) Locate MSVC.
$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
if (-not (Test-Path $vswhere)) { throw 'vswhere.exe not found - install VS Build Tools' }
# WTL needs ATL headers, so require the ATL component (fall back to plain C++ tools with a clear error).
$vs = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.ATL -property installationPath
if (-not $vs) { $vs = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.ATLMFC -property installationPath }
if (-not $vs) { throw 'no VS installation with ATL found - install "C++ ATL for latest v143 build tools" (Microsoft.VisualStudio.Component.VC.ATL)' }

# 3) Compile inside the VS dev environment (x64).
$bin = Join-Path $PSScriptRoot 'bin'
New-Item -ItemType Directory -Force $bin | Out-Null
$src = Join-Path $PSScriptRoot 'src\main.cpp'
$pb = Join-Path $PSScriptRoot 'src\proto'
$wtl = Join-Path $PSScriptRoot 'third_party\wtl'
$rc = Join-Path $PSScriptRoot 'src\lite.rc'
# Version comes from the lite installer script, so exe and setup can never disagree — the
# self-update compares this against GitHub releases. Missing iss (odd checkout) -> "dev",
# which never triggers an update.
$ver = 'dev'
$iss = Join-Path $root 'installer\agliteterm.iss'
if (Test-Path $iss) {
  $m = Select-String -Path $iss -Pattern '#define AppVersion "([^"]+)"'
  if ($m) { $ver = $m.Matches[0].Groups[1].Value }
}
$cmd = "`"$vs\VC\Auxiliary\Build\vcvars64.bat`" && rc /nologo /fo `"$bin\lite.res`" `"$rc`" && cl /nologo /O2 /W3 /EHsc /utf-8 /DUNICODE /D_UNICODE /DPB_FIELD_32BIT /DAGWL_VERSION_STR=`"\`"$ver\`"`" /I `"$pb`" /I `"$wtl`" `"$src`" `"$pb\ptyhost.pb.c`" `"$pb\pb_encode.c`" `"$pb\pb_decode.c`" `"$pb\pb_common.c`" /Fe:`"$bin\agliteterm.exe`" /Fo:`"$bin\\`" user32.lib gdi32.lib `"$bin\lite.res`""
cmd /c $cmd
if ($LASTEXITCODE -ne 0) { throw 'cl.exe failed' }

# 4) Stage the native pieces next to the exe.
Copy-Item (Join-Path $root 'native\target\release\agwinterm_core.dll') $bin -Force
Copy-Item (Join-Path $root 'native\target\release\agwinterm-ptyhost.exe') $bin -Force
Copy-Item (Join-Path $PSScriptRoot 'assets\*') $bin -Force   # bundled font + license
"built: $bin\agliteterm.exe"
