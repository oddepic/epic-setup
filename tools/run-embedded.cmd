@echo off
rem Launches epic-setup from dist using the catalog embedded in the exe
rem (the locally built catalog.json) instead of the remote oddepic/epic-setup
rem repo. This closes any running instance first so the single-instance
rem mutex can never leave an old window using a stale catalog.
setlocal
set "EXE=%~dp0..\dist\epic-setup.exe"
if not exist "%EXE%" (
  echo Error: build not found at "%EXE%".
  echo Run the publish command first and try again.
  exit /b 1
)
taskkill /IM epic-setup.exe /F >nul 2>&1
set "EPICSETUP_EMBEDDED_CATALOG=1"
set "EPICSETUP_CATALOG_SOURCE=embedded"
start "" "%EXE%" --embedded
endlocal
