@echo off
rem Dev: runs the app built from the code, on a copy of your history (your real data and installed app are not touched).
rem   dev-app.bat          copy of your history
rem   dev-app.bat -Fresh   empty, shows the first-launch setup
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\dev-app.ps1" %*
pause
