@echo off
REM Regenerate the contract fixture JSON files under webview/tests/contract/.
REM Must run over the WHOLE solution, unfiltered: the three fixture generators
REM (SpecDesk.Core.Tests, SpecDesk.Diff.Tests, SpecDesk.Contracts.Tests) live behind
REM one opt-in env var each. A narrowed `--filter` run regenerates only some of the
REM four fixture files and leaves the rest stale.
setlocal
cd /d "%~dp0.."

set UPDATE_CONTRACT_FIXTURE=1
dotnet test SpecDesk.slnx
set "TEST_EXIT=%ERRORLEVEL%"
set UPDATE_CONTRACT_FIXTURE=

if not "%TEST_EXIT%"=="0" (
  echo.
  echo Fixture regeneration FAILED (dotnet test exit code %TEST_EXIT%^).
  exit /b %TEST_EXIT%
)

echo.
echo Done. Review the diff in webview\tests\contract\ before committing.
