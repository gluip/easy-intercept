#!/usr/bin/env zsh
set -e

PROJECT="/Users/martijn/Code/easy-intercept/EasyIntercept/EasyIntercept.csproj"

echo "→ Killing existing processes..."
pkill -9 -f "dotnet.*EasyIntercept" 2>/dev/null || true
lsof -ti:8080,8888 | xargs kill -9 2>/dev/null || true
sleep 1

echo "→ Building..."
dotnet build "$PROJECT" -c Debug --nologo -v quiet

echo "→ Starting..."
dotnet run --project "$PROJECT" --no-build
