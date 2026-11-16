# Publishes Buddy from these sources into %LOCALAPPDATA%\Buddy\app and starts it from there.
# The sources' bin folder then stays free: `dotnet build` and the tests work while Buddy runs, and /aggiorna in the
# chat publishes a newer build over the installed copy. Settings, logs and conversations stay in %APPDATA%\Buddy.
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$target = Join-Path $env:LOCALAPPDATA 'Buddy\app'

$running = Get-Process -Name 'Buddy.App' -ErrorAction SilentlyContinue
if ($running) {
    Write-Host 'Buddy is running: close it (pet menu -> Esci), then run this script again.'
    exit 1
}

dotnet publish (Join-Path $root 'src\Buddy.App\Buddy.App.csproj') -c Release -o $target
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$exe = Join-Path $target 'Buddy.App.exe'
Write-Host "Installed in $target"
Write-Host 'If "Avvia con Windows" is on, Buddy points it to the installed copy at its first start.'
Start-Process -FilePath $exe
