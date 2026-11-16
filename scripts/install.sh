#!/usr/bin/env bash
# Publishes Buddy from these sources into ~/.local/share/Buddy/app and starts it from there.
# The sources' bin folder then stays free: `dotnet build` and the tests work while Buddy runs, and /aggiorna in the
# chat publishes a newer build over the installed copy. Settings, logs and conversations stay in ~/.config/Buddy.
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
target="${XDG_DATA_HOME:-$HOME/.local/share}/Buddy/app"

if pgrep -x Buddy.App >/dev/null; then
    echo 'Buddy is running: close it (pet menu -> Esci), then run this script again.'
    exit 1
fi

dotnet publish "$root/src/Buddy.App/Buddy.App.csproj" -c Release -o "$target"
echo "Installed in $target"
echo 'If "Avvia con il sistema" is on, Buddy points it to the installed copy at its first start.'
nohup "$target/Buddy.App" >/dev/null 2>&1 &
