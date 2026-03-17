#!/usr/bin/env zsh
set -e

CERT="EasyIntercept/certs/easyntercept-ca.crt"
CN="EasyIntercept Root CA"

if [[ ! -f "$CERT" ]]; then
  echo "❌ CA cert niet gevonden: $CERT"
  echo "   Start eerst de proxy zodat het certificaat wordt gegenereerd."
  exit 1
fi

echo "🔐 EasyIntercept Root CA installeren in macOS System Keychain..."
echo "   (sudo wachtwoord kan gevraagd worden)"
echo ""

sudo security add-trusted-cert -d -r trustRoot -k /Library/Keychains/System.keychain "$CERT"

echo "✅ CA geïnstalleerd en vertrouwd!"
echo ""
echo "Test met:"
echo "  curl -x http://localhost:8888 https://httpbin.org/get"
echo ""
echo "Verwijderen:"
echo "  sudo security delete-certificate -c \"$CN\" /Library/Keychains/System.keychain"
