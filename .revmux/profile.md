# Project conventions

agwinterm is a Windows terminal for AI coding agents, built on .NET with a
native layer beside it (`native/`). The lite product moved to its own repository
(agliteterm) at 0.17.5; `lite/` holds only the frozen handover installer.

## Build and test

```bash
dotnet build Agwinterm.slnx -c Release
dotnet test Agwinterm.slnx -c Release
```

`native/` builds separately (`cargo build --release`, `cargo test`). The core C
ABI is declared by `ABI_VERSION` in `native/agwinterm-core/src/lib.rs` and
`RequiredAbi` in `src/Agwinterm.Core/RustEmulatorCore.cs`; they must agree.

## Review bar

- Report real behavioral defects, dropped data, unhandled exceptions, unsafe
  resource lifetimes, and silent fallbacks that hide caller mistakes.
- Treat ConPTY, control-pipe, and pty-host boundary defects as high risk.
- Treat incompatible control-API request or response changes as defects.
- Check the implementation against its plan and require tests for new code paths.
- Do not report style preferences, UI behavior that only visual inspection can
  confirm, or work explicitly deferred by the plan.
