#!/usr/bin/env bash
#
# Dev loop: stop any running instance, rebuild the frontend and backend, then run.
#
#   ./restart.sh
#   UI_PORT=8080 ./restart.sh     # if you run the UI on a non-default port
set -e

ROOT="$(cd "$(dirname "$0")" && pwd)"
PROJECT="$ROOT/EasyIntercept/EasyIntercept.csproj"
UI_PORT="${UI_PORT:-1337}"
# Fixed in the app (Hosting/StartupOptions.ProxyPort); not configurable, so not overridable here.
PROXY_PORT=9999

echo "→ Killing existing processes..."
pkill -9 -f "dotnet.*EasyIntercept" 2>/dev/null || true
lsof -ti:"$UI_PORT","$PROXY_PORT" 2>/dev/null | xargs kill -9 2>/dev/null || true
sleep 1

echo "→ Building frontend..."
(cd "$ROOT/frontend" && npx vite build --emptyOutDir) 2>&1 | tail -3

echo "→ Building backend..."
dotnet build "$PROJECT" -c Debug --nologo -v quiet

# Keep dev data (sessions, certs, mock rules) in the project folder instead of ~/.local/share/EasyIntercept
export DataRoot="$ROOT/EasyIntercept"
export UiPort="$UI_PORT"

echo "→ Starting... (UI http://localhost:$UI_PORT, proxy $PROXY_PORT)"
dotnet run --project "$PROJECT" --no-build
