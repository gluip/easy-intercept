#!/usr/bin/env zsh
set -e

IFACE="Wi-Fi"

echo "→ Disabling HTTP proxy..."
networksetup -setwebproxystate "$IFACE" off

echo "→ Disabling HTTPS proxy..."
networksetup -setsecurewebproxystate "$IFACE" off

echo "✅ System proxy OFF"
