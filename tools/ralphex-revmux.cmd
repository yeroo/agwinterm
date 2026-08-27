@echo off
REM Windows entry point for the ralphex -> revmux review bridge.
REM Ralphex 1.6.1 cannot execute a .cmd directly, so project configuration uses
REM Git Bash plus a shell-safe prompt. This remains a convenient manual wrapper.
REM %~dp0 is this file's directory, so the pair stays relocatable.
setlocal
set "BASH=%ProgramFiles%\Git\bin\bash.exe"
if not exist "%BASH%" set "BASH=%ProgramFiles%\Git\usr\bin\bash.exe"
if not exist "%BASH%" (
  echo ralphex-revmux: git bash not found; install Git for Windows or edit this wrapper 1>&2
  exit /b 127
)
"%BASH%" "%~dp0ralphex-revmux.sh" "%~1"
exit /b %ERRORLEVEL%
