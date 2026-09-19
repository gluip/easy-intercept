#!/usr/bin/env bash
#
# EasyIntercept smoke tests — run against an already-running instance.
#
#   ./test.sh                     # standard ports (UI 1337, proxy 9999)
#   UI_PORT=8080 ./test.sh        # if you changed UiPort
#
# The HTTPS tests need the EasyIntercept CA to be trusted by the OS
# (install-ca.sh / install-ca.ps1, or EasyIntercept.exe --install-ca).
#
# No `set -e` on purpose: the script keeps its own pass/fail tally and must
# report every failing check instead of aborting at the first one.

API="http://localhost:${UI_PORT:-1337}"
# Fixed in the app (Hosting/StartupOptions.ProxyPort); not configurable.
PROXY="http://localhost:9999"
PASS=0
FAIL=0

pass() { echo "✓ $1"; PASS=$((PASS + 1)); }
fail() { echo "✗ $1"; FAIL=$((FAIL + 1)); }

# curl built against Schannel (Windows / Git Bash) insists on a revocation check, which
# certificates minted by a local intercepting proxy can never satisfy. macOS curl does not.
TLS_OPTS=()
if curl --version | head -1 | grep -qi schannel; then
  TLS_OPTS=(--ssl-no-revoke)
fi

# check <description> <command...> — passes when the command succeeds
check() {
  local desc="$1"
  shift
  if "$@" >/dev/null 2>&1; then pass "$desc"; else fail "$desc"; fi
}

# json <js-expression over `d`> — reads stdin, prints a value, empty on parse failure.
# Uses node (already required to build the frontend) so this runs on macOS and Git Bash alike.
json() {
  node -e "
    let s = '';
    process.stdin.on('data', c => s += c).on('end', () => {
      try { const d = JSON.parse(s); console.log($1); } catch (e) { process.exit(1); }
    });
  " 2>/dev/null
}

echo "=== EasyIntercept smoke tests ==="
echo "  UI:    $API"
echo "  proxy: $PROXY"
echo ""

# --- Web UI and API ---

check "Web UI reachable ($API/)" curl -sf --max-time 3 "$API/" -o /dev/null

INFO=$(curl -sf --max-time 3 "$API/api/info")
INFO_PORTS=$(echo "$INFO" | json "d.uiPort + ':' + d.proxyPort")
if [[ -n "$INFO_PORTS" ]]; then
  pass "/api/info reports ports $INFO_PORTS"
  echo "    version: $(echo "$INFO" | json "d.version")"
else
  fail "/api/info did not return usable JSON"
fi

SESSION_COUNT=$(curl -sf --max-time 3 "$API/api/sessions" | json "d.length")
if [[ -n "$SESSION_COUNT" ]]; then
  pass "Sessions API returns a JSON array ($SESSION_COUNT sessions)"
else
  fail "Sessions API unreachable or not an array"
fi

RULE_COUNT=$(curl -sf --max-time 3 "$API/api/auto-responders" | json "d.length")
if [[ -n "$RULE_COUNT" ]]; then
  pass "Auto-responder API returns a JSON array ($RULE_COUNT rules)"
else
  fail "Auto-responder API unreachable or not an array"
fi

BROWSERS=$(curl -sf --max-time 3 "$API/api/browser-launch" | json "d.browsers.length")
if [[ -n "$BROWSERS" ]]; then
  pass "Browser-launch API returns a browser list ($BROWSERS detected)"
else
  fail "Browser-launch API unreachable or malformed"
fi

check "System-proxy API readable" curl -sf --max-time 3 "$API/api/system-proxy" -o /dev/null
check "Mobile CA install page served (/install)" curl -sf --max-time 3 "$API/install" -o /dev/null

# --- HTTP proxying ---

BEFORE=${SESSION_COUNT:-0}

BODY=$(curl -sf --max-time 20 -x "$PROXY" http://httpbin.org/get)
if echo "$BODY" | grep -q '"url"'; then
  pass "HTTP proxy forwards request (httpbin.org/get)"
else
  fail "HTTP proxy did not return the expected response"
fi

AFTER=$(curl -sf --max-time 3 "$API/api/sessions" | json "d.length")
if [[ -n "$AFTER" && "$AFTER" -gt "$BEFORE" ]]; then
  pass "Proxied request was captured as a session ($BEFORE → $AFTER)"
else
  fail "Session count did not grow after a proxied request"
fi

# --- LLM image responses ---
# Gemini image models return the picture as base64 inside the JSON body. The UI can only
# draw it if that body reaches the session store byte for byte, so serve one through a
# temporary auto-responder rule (no API key needed) and compare what comes back.

IMG_URL="http://generativelanguage.googleapis.com/v1beta/models/smoke-test-image:generateContent"
IMG_B64="iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg=="
IMG_BODY="{\"candidates\":[{\"content\":{\"role\":\"model\",\"parts\":[{\"inlineData\":{\"mimeType\":\"image/png\",\"data\":\"$IMG_B64\"}}]},\"finishReason\":\"STOP\"}],\"modelVersion\":\"smoke-test-image\"}"
IMG_RULE=$(node -e "console.log(JSON.stringify({ name: 'smoke test: gemini image', method: 'POST', url: process.argv[1],
  responseStatus: 200, responseHeaders: { 'Content-Type': 'application/json; charset=UTF-8' }, responseBody: process.argv[2] }))" \
  "$IMG_URL" "$IMG_BODY")

RULE_ID=$(curl -sf --max-time 3 -H "Content-Type: application/json" -d "$IMG_RULE" "$API/api/auto-responders" | json "d.id")
if [[ -n "$RULE_ID" ]]; then
  GOT=$(curl -sf --max-time 10 -x "$PROXY" -H "Content-Type: application/json" \
    -d '{"contents":[{"parts":[{"text":"a single pixel"}]}]}' "$IMG_URL" \
    | json "d.candidates[0].content.parts[0].inlineData.data")
  if [[ "$GOT" == "$IMG_B64" ]]; then
    pass "Image response reaches the client intact"
  else
    fail "Image response was altered on its way to the client"
  fi

  STORED=$(curl -sf --max-time 3 "$API/api/sessions" \
    | json "JSON.parse(d.filter(s => s.url === '$IMG_URL').pop().responseBody).candidates[0].content.parts[0].inlineData.data")
  if [[ "$STORED" == "$IMG_B64" ]]; then
    pass "Image response stored intact for the UI"
  else
    fail "Stored image response is missing or altered"
  fi

  check "Temporary image rule removed" curl -sf --max-time 3 -X DELETE "$API/api/auto-responders/$RULE_ID"
else
  fail "Could not create the temporary image auto-responder rule"
fi

# --- CA certificate ---

CA_FILE=$(mktemp)
if curl -sf --max-time 3 -o "$CA_FILE" "$API/ca" && head -1 "$CA_FILE" | grep -q "BEGIN CERTIFICATE"; then
  pass "CA cert downloadable (/ca endpoint)"
else
  fail "CA cert endpoint broken"
fi
rm -f "$CA_FILE"

# --- HTTPS proxying (requires the CA to be trusted) ---

HTTPS_BODY=$(curl -sf --max-time 20 "${TLS_OPTS[@]}" -x "$PROXY" https://httpbin.org/get)
if echo "$HTTPS_BODY" | grep -q '"url"'; then
  pass "HTTPS proxy forwards request (httpbin.org/get)"
else
  fail "HTTPS proxy did not return the expected response — is the CA trusted?"
fi

if curl -sf --max-time 20 "${TLS_OPTS[@]}" -x "$PROXY" https://nos.nl | grep -q 'nos'; then
  pass "HTTPS proxy forwards request (nos.nl)"
else
  fail "HTTPS proxy did not return the expected response (nos.nl)"
fi

HTTPS_COUNT=$(curl -sf --max-time 3 "$API/api/sessions" | json "d.filter(s => s.url.startsWith('https://')).length")
if [[ -n "$HTTPS_COUNT" && "$HTTPS_COUNT" -gt 0 ]]; then
  pass "HTTPS sessions stored ($HTTPS_COUNT)"
else
  fail "No HTTPS sessions stored"
fi

echo ""
echo "=== Results: $PASS passed, $FAIL failed ==="
[[ $FAIL -eq 0 ]]
