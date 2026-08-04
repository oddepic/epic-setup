@echo off
rem Launches EpicSetup using the catalog embedded in the exe (the locally built
rem catalog.json) instead of the remote oddepic/epic-setup repo. Use this to
rem test catalog changes before they're pushed to GitHub.
set EPICSETUP_EMBEDDED_CATALOG=1
start "" "%~dp0..\dist\EpicSetup.exe"
