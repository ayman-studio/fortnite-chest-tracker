@echo off
chcp 65001 >nul
title Building Fortnite - Chest Tracker
cd /d "%~dp0"

rem .NET 10 SDK: the one on the PATH, else a per-user install (dotnet-install.ps1 puts it in %LOCALAPPDATA%\Microsoft\dotnet).
set "DOTNET=dotnet"
dotnet --list-sdks 2>nul | findstr /b "10." >nul
if errorlevel 1 (
  if exist "%LOCALAPPDATA%\Microsoft\dotnet\dotnet.exe" (
    set "DOTNET=%LOCALAPPDATA%\Microsoft\dotnet\dotnet.exe"
    set "DOTNET_ROOT=%LOCALAPPDATA%\Microsoft\dotnet"
  )
)
"%DOTNET%" --list-sdks 2>nul | findstr /b "10." >nul
if errorlevel 1 (
  echo The .NET 10 SDK is not installed.
  echo Download it here: https://dotnet.microsoft.com/download/dotnet/10.0  ^(".NET SDK", Windows x64^)
  echo Then run build.bat again.
  pause
  exit /b 1
)

rem Optional: set FCT_REPO=owner/name to let this exe update itself from that repository's releases.
rem Without it, updates are unavailable in the built exe (a local build is never replaced by someone else's release).
echo Building (1 to 2 minutes the first time)...
"%DOTNET%" publish src\FortniteChestTracker\FortniteChestTracker.csproj -c Release -r win-x64 --self-contained true ^
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true ^
  -p:EnableCompressionInSingleFile=true -p:DebugType=none -p:TrackerRepo=%FCT_REPO% -o dist
if errorlevel 1 (
  echo.
  echo The build failed. See the message above.
  pause
  exit /b 1
)

echo.
echo Done! The file to share: %~dp0dist\FortniteChestTracker.exe
explorer dist
pause
