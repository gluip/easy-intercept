#!/usr/bin/env zsh
set -e

IFACE="Wi-Fi"

echo "→ Enabling HTTP proxy (localhost:9999)..."
networksetup -setwebproxy "$IFACE" localhost 9999

echo "→ Enabling HTTPS proxy (localhost:9999)..."
networksetup -setsecurewebproxy "$IFACE" localhost 9999

echo "✅ System proxy ON → localhost:9999"
