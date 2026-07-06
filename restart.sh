#!/usr/bin/env zsh
set -e

ROOT="$(cd "$(dirname "$0")" && pwd)"
PROJECT="$ROOT/EasyIntercept/EasyIntercept.csproj"

echo "→ Killing existing processes..."
pkill -9 -f "dotnet.*EasyIntercept" 2>/dev/null || true
lsof -ti:8080,9999 | xargs kill -9 2>/dev/null || true
sleep 1

echo "→ Building frontend..."
(cd "$ROOT/frontend" && npx vite build --emptyOutDir) 2>&1 | tail -3

echo "→ Building backend..."
dotnet build "$PROJECT" -c Debug --nologo -v quiet

echo "→ Starting..."
dotnet run --project "$PROJECT" --no-build
