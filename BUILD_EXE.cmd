@echo off
rem BUTTON: build the new Recognition exe (and the downloadable zip).
rem Closes a running Recognition, builds, runs the tests, packages to dist\Recognition-win-x64.zip, then opens dist\.
where pwsh >nul 2>nul || (echo PowerShell 7 is required: winget install Microsoft.PowerShell & pause & exit /b 2)
cd /d "%~dp0"
taskkill /im RecognitionBrowser.exe /f >nul 2>nul
dotnet build browser -c Release || goto fail
dotnet run --project browser.tests -c Release || goto fail
pwsh -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\RUN_PACKAGE_DIST_V1.ps1" -RepoRoot "%~dp0." || goto fail
echo.
echo DONE. The zip is in dist\Recognition-win-x64.zip
start "" "%~dp0dist"
pause
exit /b 0
:fail
echo.
echo BUILD FAILED - read the message above.
pause
exit /b 1
