@echo off
REM Re-issue the single-file Windows release exe.
REM Build config lives in src\SpecDesk.Host\Properties\PublishProfiles\win-x64.pubxml.
REM Result: one self-contained SpecDesk.Host.exe (no .NET runtime needed on the target machine;
REM it still needs the Microsoft Edge WebView2 runtime, pre-installed on Win11 and most Win10).
REM Requires node/npm on PATH (the publish runs the esbuild webview bundle).
setlocal
cd /d "%~dp0"

dotnet publish src/SpecDesk.Host -p:PublishProfile=win-x64 -p:DebugType=none
if errorlevel 1 (
  echo.
  echo Release build FAILED.
  pause
  exit /b 1
)

echo.
echo Done. Single exe to share:
echo   %~dp0src\SpecDesk.Host\bin\Release\net10.0\win-x64\publish\SpecDesk.Host.exe
pause
