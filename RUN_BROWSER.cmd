@echo off
rem BUTTON: build and open Recognition straight from the source folder (quick try-out, no install).
where pwsh >nul 2>nul || (echo PowerShell 7 is required: winget install Microsoft.PowerShell & pause & exit /b 2)
pwsh -NoProfile -ExecutionPolicy Bypass -File "%~dp0browser\build.ps1" -Run
pause
