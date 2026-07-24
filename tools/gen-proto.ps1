# Regenerate all protobuf handlers from proto/ptyhost.proto (generated code is
# COMMITTED so CI and dev builds never need protoc/python — rerun this after
# schema changes). C#: protoc's csharp plugin. Rust: prost via a tiny generator
# crate. C++ (lite): nanopb (small C runtime, fits lite's footprint ethos).
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$protoc = Join-Path $PSScriptRoot 'protoc\bin\protoc.exe'
$proto = Join-Path $root 'proto'

# C# -> src/Agwinterm.Pty/Proto/
$csOut = Join-Path $root 'src\Agwinterm.Pty\Proto'
New-Item -ItemType Directory -Force $csOut | Out-Null
& $protoc -I $proto --csharp_out=$csOut (Join-Path $proto 'ptyhost.proto')
if ($LASTEXITCODE -ne 0) { throw 'protoc csharp failed' }

# Rust -> native/agwinterm-ptyhost/src/proto.rs (prost)
Push-Location (Join-Path $PSScriptRoot 'prost-gen')
$env:PROTOC = $protoc
cargo run --quiet
Pop-Location
if ($LASTEXITCODE -ne 0) { throw 'prost generation failed' }

# C++ (nanopb) -> lite/src/proto/ (nanopb's generator shells out to protoc: put ours on PATH)
$env:PATH = (Join-Path $PSScriptRoot 'protoc\bin') + ';' + $env:PATH
$nanopb = Join-Path $PSScriptRoot 'nanopb'
$liteOut = Join-Path $root 'lite\src\proto'
New-Item -ItemType Directory -Force $liteOut | Out-Null
python (Join-Path $nanopb 'generator\nanopb_generator.py') -I $proto -D $liteOut (Join-Path $proto 'ptyhost.proto')
if ($LASTEXITCODE -ne 0) { throw 'nanopb generation failed' }
Copy-Item (Join-Path $nanopb 'pb.h'), (Join-Path $nanopb 'pb_encode.*'), (Join-Path $nanopb 'pb_decode.*'), (Join-Path $nanopb 'pb_common.*') $liteOut -Force

"generated: C# ($csOut), Rust (native/agwinterm-ptyhost/src/proto.rs), C++ ($liteOut)"
