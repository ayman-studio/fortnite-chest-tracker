# Dev: assembles the website like Cloudflare Pages does (site\build.sh) into .dev\site and serves that folder at the root:
# http://127.0.0.1:8080/ (fr: /fr/, es: /es/), like https://fortnite-chest-tracker.com/. Run it again after a change.
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$bash = @("$env:ProgramFiles\Git\bin\bash.exe", "${env:ProgramFiles(x86)}\Git\bin\bash.exe") | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $bash) { throw 'Git Bash not found (Git for Windows is needed to run site\build.sh)' }

$out = Join-Path $root '.dev\site'
Push-Location $root
try { & $bash site/build.sh '.dev/site' } finally { Pop-Location }
if ($LASTEXITCODE -ne 0) { throw 'Site build failed' }

# One server at a time on port 8080
Get-CimInstance Win32_Process -Filter "Name='python.exe'" | Where-Object { $_.CommandLine -like '*http.server 8080*' } |
    ForEach-Object { Stop-Process -Id $_.ProcessId -Force }
Start-Process python -ArgumentList '-m', 'http.server', '8080', '--bind', '127.0.0.1', '--directory', "`"$out`"" -WindowStyle Minimized
Start-Sleep -Seconds 1
Start-Process 'http://127.0.0.1:8080/'
Write-Host 'Site served at http://127.0.0.1:8080/ (close the minimized Python window to stop it).'
