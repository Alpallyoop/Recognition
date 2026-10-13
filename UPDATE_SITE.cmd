@echo off
rem BUTTON: update the website (theconstellationnetwork.com) from the Constellation folder: validate, rebuild site data, refresh downloads, commit, push.
if not exist "%~dp0..\Constellation\UPDATE_SITE.cmd" (echo Constellation folder not found next to this one. & pause & exit /b 2)
call "%~dp0..\Constellation\UPDATE_SITE.cmd" %*
