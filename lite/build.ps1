# Build agwinterm-lite: MSVC cl.exe (located via vswhere), plus the Rust workspace
# it depends on. Output: lite\bin\ with the exe + agwinterm_core.dll + agwinterm-ptyhost.exe.
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent

# 1) The Rust server + core the client rides on.
cargo build --release --manifest-path (Join-Path $root 'native\Cargo.toml')
if ($LASTEXITCODE -ne 0) { throw 'cargo build failed' }

# 2) Locate MSVC.
$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
if (-not (Test-Path $vswhere)) { throw 'vswhere.exe not found - install VS Build Tools' }
$vs = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $vs) { throw 'no VS installation with C++ tools found' }

# 3) Compile inside the VS dev environment (x64).
$bin = Join-Path $PSScriptRoot 'bin'
New-Item -ItemType Directory -Force $bin | Out-Null
$src = Join-Path $PSScriptRoot 'src\main.cpp'
$cmd = "`"$vs\VC\Auxiliary\Build\vcvars64.bat`" && cl /nologo /O2 /W3 /EHsc /DUNICODE /D_UNICODE `"$src`" /Fe:`"$bin\agwinterm-lite.exe`" /Fo:`"$bin\\`" user32.lib gdi32.lib"
cmd /c $cmd
if ($LASTEXITCODE -ne 0) { throw 'cl.exe failed' }

# 4) Stage the native pieces next to the exe.
Copy-Item (Join-Path $root 'native\target\release\agwinterm_core.dll') $bin -Force
Copy-Item (Join-Path $root 'native\target\release\agwinterm-ptyhost.exe') $bin -Force
"built: $bin\agwinterm-lite.exe"
