@echo off
setlocal
rem BUTTON: update the website (theconstellationnetwork.com) from the Constellation folder: validate, rebuild site data, refresh downloads, commit, push.
rem The Constellation folder is looked up relative to this one (it moved under Constellation\Constellation-Network), so no drive or fixed path is assumed.
set "CN="
for %%P in ("%~dp0..\..\Constellation-Network" "%~dp0..\Constellation-Network" "%~dp0..\Constellation" "%~dp0..\..\..\Constellation\Constellation-Network") do if not defined CN if exist "%%~fP\UPDATE_SITE.cmd" set "CN=%%~fP"
if not defined CN (echo Constellation folder not found near this one. & pause & exit /b 2)
call "%CN%\UPDATE_SITE.cmd" %*
