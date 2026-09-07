#!/usr/bin/env bash
#
# Installeert de root CA van EasyIntercept in de macOS System Keychain.
#
#   ./install-ca.sh                      # CA van de draaiende app (of de default data-root)
#   ./install-ca.sh pad/naar/ca.crt      # expliciet certificaat
#   UI_PORT=8080 ./install-ca.sh         # als de UI op een andere poort draait
#
# Zonder argument wordt eerst de draaiende app gevraagd (http://localhost:<UI_PORT>/ca), zodat
# je gegarandeerd de CA vertrouwt die op dít moment de host-certs ondertekent. Daarna vallen we
# terug op $DataRoot, de default data-root en tenslotte de dev-map in deze repo.
set -e

CN="EasyIntercept Root CA"
CRT="easyntercept-ca.crt"
UI_PORT="${UI_PORT:-1337}"
ROOT="$(cd "$(dirname "$0")" && pwd)"
DEFAULT_DATA_ROOT="$HOME/Library/Application Support/EasyIntercept"

CERT=""
CLEANUP=""
trap '[[ -n "$CLEANUP" ]] && rm -f "$CLEANUP"' EXIT

if [[ -n "$1" ]]; then
  CERT="$1"
  if [[ ! -f "$CERT" ]]; then
    echo "❌ CA cert niet gevonden: $CERT"
    exit 1
  fi
  echo "📄 Certificaat: $CERT"
else
  # 1. De draaiende app — dit is de enige bron die zeker klopt.
  TMP="$(mktemp -t easyntercept-ca)"
  if curl -fsS --max-time 5 "http://localhost:$UI_PORT/ca" -o "$TMP" 2>/dev/null; then
    CERT="$TMP"
    CLEANUP="$TMP"
    echo "📄 Certificaat opgehaald bij de draaiende app (poort $UI_PORT)"
  else
    rm -f "$TMP"
    # 2. $DataRoot, 3. default data-root, 4. dev-map in de repo.
    for dir in "$DataRoot" "$DEFAULT_DATA_ROOT" "$ROOT/EasyIntercept"; do
      [[ -n "$dir" && -f "$dir/certs/$CRT" ]] && { CERT="$dir/certs/$CRT"; break; }
    done
    if [[ -z "$CERT" ]]; then
      echo "❌ Geen CA cert gevonden."
      echo "   Start EasyIntercept eerst (dan wordt het certificaat gegenereerd), of geef het pad mee:"
      echo "   ./install-ca.sh pad/naar/$CRT"
      exit 1
    fi
    echo "⚠️  EasyIntercept draait niet op poort $UI_PORT — certificaat van schijf gebruikt:"
    echo "   $CERT"
    echo "   Draait de app met een andere DataRoot, dan is dit niet de CA die hij gebruikt."
  fi
fi

FINGERPRINT="$(openssl x509 -in "$CERT" -noout -fingerprint -sha256 | cut -d= -f2)"
echo "   SHA-256: $FINGERPRINT"
echo ""

# Al een (andere) EasyIntercept CA vertrouwd? Dat is precies wat certificaatfouten in de
# proxied browser veroorzaakt, dus meld het.
STALE="$(security find-certificate -a -c "$CN" -Z /Library/Keychains/System.keychain 2>/dev/null \
  | awk '/SHA-256 hash:/ {print $3}' \
  | grep -vi "$(echo "$FINGERPRINT" | tr -d ':')" || true)"
if [[ -n "$STALE" ]]; then
  echo "⚠️  Er staat al een ándere EasyIntercept CA in de System Keychain:"
  echo "$STALE" | sed 's/^/     /'
  echo "   Verwijder die na afloop met:"
  echo "     sudo security delete-certificate -c \"$CN\" /Library/Keychains/System.keychain"
  echo ""
fi

echo "🔐 EasyIntercept Root CA installeren in macOS System Keychain..."
echo "   (sudo wachtwoord kan gevraagd worden)"
echo ""

sudo security add-trusted-cert -d -r trustRoot -k /Library/Keychains/System.keychain "$CERT"

echo "✅ CA geïnstalleerd en vertrouwd!"
echo ""
echo "Test met:"
echo "  curl -x http://localhost:9999 https://httpbin.org/get"
echo ""
echo "Sluit een al geopende proxied browser volledig af en start hem opnieuw."
echo ""
echo "Verwijderen:"
echo "  sudo security delete-certificate -c \"$CN\" /Library/Keychains/System.keychain"
