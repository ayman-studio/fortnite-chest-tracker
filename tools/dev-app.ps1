# Dev: builds the app (Debug) and runs it on its own data folder (.dev\app\data), next to the installed app.
# First run: a COPY of your real history (read only: %APPDATA%\FortniteChestTracker, or the folder of versions 1.0.x
# when the installed app has not moved it yet; neither is ever written). -Fresh: empty folder, the first-launch setup
# shows. The page opens in the browser; the dev instance takes the next free port (5318…).
param([switch]$Fresh)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$dotnetDir = Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet'
$dotnet = if (Test-Path (Join-Path $dotnetDir 'dotnet.exe')) { $env:DOTNET_ROOT = $dotnetDir; Join-Path $dotnetDir 'dotnet.exe' } else { 'dotnet' }
$exe = Join-Path $root 'src\FortniteChestTracker\bin\Debug\net10.0\FortniteChestTracker.exe'
$data = Join-Path $root '.dev\app\data'

# A previous dev instance holds the exe: stop it (only the one built here, never the installed app)
Get-CimInstance Win32_Process -Filter "Name='FortniteChestTracker.exe'" | Where-Object { $_.ExecutablePath -eq $exe } |
    ForEach-Object { Stop-Process -Id $_.ProcessId -Force }
Start-Sleep -Milliseconds 500

Write-Host 'Build (Debug)...'
& $dotnet build (Join-Path $root 'src\FortniteChestTracker') -c Debug -nologo -v q -clp:ErrorsOnly
if ($LASTEXITCODE -ne 0) { throw 'Build failed' }

if ($Fresh -and (Test-Path $data)) { Remove-Item $data -Recurse -Force }
if (-not (Test-Path (Join-Path $data 'config.json')) -and -not $Fresh) {
    New-Item -ItemType Directory -Force $data | Out-Null
    $real = Join-Path $env:APPDATA 'FortniteChestTracker'
    if (-not (Test-Path $real)) { $real = Join-Path $env:APPDATA 'ChestCounter' } # versions 1.0.x
    foreach ($f in 'data.json', 'treasures.json') {
        if (Test-Path (Join-Path $real $f)) { Copy-Item (Join-Path $real $f) $data }
    }
    $lang = 'fr'
    try { $lang = (Get-Content (Join-Path $real 'config.json') -Raw | ConvertFrom-Json).Language } catch { }
    # Never "open with Fortnite" (it would move the real Run key) and no updates in dev.
    [ordered]@{
        Language = $lang; DemosFolder = (Join-Path $env:LOCALAPPDATA 'FortniteGame\Saved\Demos')
        ShowOverlay = $true; StartWithFortnite = $false; OpenUiOnStart = $true; UpdateMode = 'off'
    } | ConvertTo-Json | Set-Content (Join-Path $data 'config.json') -Encoding UTF8
    Write-Host "Dev data: copy of your history ($real) in $data"
}
elseif ($Fresh) {
    New-Item -ItemType Directory -Force $data | Out-Null
    Write-Host 'Dev data: empty (first-launch setup).'
}

Start-Process $exe -ArgumentList '--data', "`"$data`""
Write-Host 'Dev app started: the page opens in your browser (tray icon next to the clock, Quit to stop it).'
