@echo off
rem Dev: builds the website locally and opens it (nothing is published).
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\dev-site.ps1"
pause
