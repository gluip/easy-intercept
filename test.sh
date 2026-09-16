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

# --- Streaming: the client must see bytes before the response ends ---

# httpbin drips 5 bytes over 2 s. A buffering proxy holds the first byte until the end (~2 s);
# a streaming one delivers it right away, so time-to-first-byte must be well under the total.
DRIP=$(curl -s --max-time 20 -x "$PROXY" -N -o /dev/null -w '%{http_code} %{time_starttransfer} %{time_total}' \
  'http://httpbin.org/drip?numbytes=5&duration=2&delay=0')
if echo "$DRIP" | awk '{ exit !($1 == 200 && $2 < 1.0 && $3 >= 1.5) }'; then
  pass "Streaming: first byte arrived before the stream ended (code ttfb total: $DRIP)"
else
  fail "Streaming: response was buffered or failed (code ttfb total: '$DRIP')"
fi

STREAM_LINES=$(curl -sN --max-time 20 -x "$PROXY" http://httpbin.org/stream/3 | grep -c '"url"')
if [[ "$STREAM_LINES" == "3" ]]; then
  pass "Chunked: httpbin.org/stream/3 relayed as 3 JSON lines"
else
  fail "Chunked: expected 3 JSON lines, got '$STREAM_LINES'"
fi

check "HEAD request completes without hanging" curl -sI --max-time 5 -x "$PROXY" http://httpbin.org/get -o /dev/null

DRIP_SESSION=$(curl -sf --max-time 3 "$API/api/sessions" | json "(s => s ? [s.responseComplete, s.timeToFirstByteMs, s.durationMs].join(' ') : '')(d.find(x => x.url.includes('/drip')))")
read -r DRIP_COMPLETE DRIP_TTFB DRIP_TOTAL <<<"$DRIP_SESSION"
if [[ "$DRIP_COMPLETE" == "true" && -n "$DRIP_TTFB" && "$DRIP_TTFB" -lt "$DRIP_TOTAL" ]]; then
  pass "Streamed session recorded as complete with ttfb < duration (${DRIP_TTFB}ms < ${DRIP_TOTAL}ms)"
else
  fail "Streamed session not recorded properly (responseComplete ttfb duration: '$DRIP_SESSION')"
fi

# --- WebSocket handshake (the message relay itself is covered by the .NET integration tests) ---

# curl has no WebSocket client mode, but it will happily send the upgrade request; a 101 back through the
# proxy proves the upstream handshake, the accept key and the tunnel detection. curl then waits for a body
# that never comes, so the timeout exit code is expected.
WS_HEAD=$(curl -s -i --http1.1 --max-time 6 "${TLS_OPTS[@]}" -x "$PROXY" \
  -H 'Upgrade: websocket' -H 'Connection: Upgrade' -H 'Sec-WebSocket-Version: 13' \
  -H 'Sec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==' https://echo.websocket.org/ 2>/dev/null | head -c 2000)
if echo "$WS_HEAD" | grep -q "HTTP/1.1 101" && echo "$WS_HEAD" | grep -qi "Sec-WebSocket-Accept: s3pPLMBiTxaQ9kYGzzhZRbK+xOo="; then
  pass "WebSocket: upgrade through the proxy answered with 101 and the right accept key"
else
  fail "WebSocket: no 101 for wss://echo.websocket.org (got: $(echo "$WS_HEAD" | grep -m1 'HTTP/1.1 [^2]' || echo "$WS_HEAD" | head -1))"
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
