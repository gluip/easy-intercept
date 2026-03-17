#!/usr/bin/env zsh
set -e

IFACE="Wi-Fi"

echo "→ Enabling HTTP proxy (localhost:8888)..."
networksetup -setwebproxy "$IFACE" localhost 8888

echo "→ Enabling HTTPS proxy (localhost:8888)..."
networksetup -setsecurewebproxy "$IFACE" localhost 8888

echo "✅ System proxy ON → localhost:8888"
