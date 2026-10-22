@echo off
rem BUTTON: stress-test Recognition. Builds the browser, runs the browser tests 3 times (they must agree), then the stress tests
rem (volume, random and hostile input, concurrency) and the page-script tests. Installs nothing and does not start the browser.
rem Takes a few minutes. Log: runtime\stress_logs.  Options can be added after the name, for example:  STRESS_TESTS.cmd -Rounds 5
rem                                                                                   or  STRESS_TESTS.cmd -Quick
where pwsh >nul 2>nul || (echo PowerShell 7 is required: winget install Microsoft.PowerShell & pause & exit /b 2)
pwsh -NoProfile -ExecutionPolicy Bypass -File "%~dp0..\..\scripts\RUN_STRESS_TESTS_V1.ps1" -RepoRoot "%~dp0..\.." %*
echo.
pause
