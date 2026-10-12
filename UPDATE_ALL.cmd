@echo off
rem One command: update the source repos, run tests + gate, package, close Recognition, install, reopen it.
where pwsh >nul 2>nul || (echo PowerShell 7 is required: winget install Microsoft.PowerShell & pause & exit /b 2)
pwsh -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\UPDATE_ALL_V1.ps1" -RepoRoot "%~dp0." -CloseApp -Relaunch %*
echo.
pause
