#!/usr/bin/env zsh
set -e

PROXY="http://localhost:9999"
API="http://localhost:8080"
PASS=0
FAIL=0

pass() { echo "✓ $1"; PASS=$((PASS+1)); }
fail() { echo "✗ $1"; FAIL=$((FAIL+1)); }

echo "=== EasyIntercept smoke tests ==="
echo ""

# 1. Web UI reachable
if curl -sf --max-time 3 "$API/" -o /dev/null; then
  pass "Web UI reachable ($API/)"
else
  fail "Web UI not reachable"
fi

# 2. Sessions API
SESSIONS=$(curl -sf --max-time 3 "$API/api/sessions")
if [[ $? -eq 0 ]]; then
  pass "Sessions API returns JSON"
else
  fail "Sessions API unreachable"
fi

# 3. HTTP proxy — forward request
BODY=$(curl -sf --max-time 20 -x "$PROXY" http://httpbin.org/get)
if echo "$BODY" | grep -q '"url"'; then
  pass "HTTP proxy forwards request (httpbin.org/get)"
else
  fail "HTTP proxy did not return expected response"
fi

# 4. Session was stored
COUNT=$(curl -sf --max-time 3 "$API/api/sessions" | python3 -c "import sys,json; d=json.load(sys.stdin); print(len(d))")
if [[ $COUNT -gt 0 ]]; then
  pass "Session stored in memory ($COUNT sessions)"
else
  fail "No sessions stored after request"
fi

# 5. Pin a session and verify pinned response is returned
SESSION_ID=$(curl -sf --max-time 3 "$API/api/sessions" | python3 -c "import sys,json; d=json.load(sys.stdin); print(d[0]['id']) if d else print('')")
SESSION_URL=$(curl -sf --max-time 3 "$API/api/sessions" | python3 -c "import sys,json; d=json.load(sys.stdin); print(d[0]['url']) if d else print('')")

if [[ -n "$SESSION_ID" ]]; then
  PIN_RESULT=$(curl -sf --max-time 3 -X POST "$API/api/sessions/$SESSION_ID/pin")
  if echo "$PIN_RESULT" | grep -q "pinned"; then
    pass "Pin session ($SESSION_ID)"

    # Verify pinned response is served
    PINNED_HEADER=$(curl -sI --max-time 20 -x "$PROXY" "$SESSION_URL" | grep -i "X-EasyIntercept-Pinned" || true)
    if [[ -n "$PINNED_HEADER" ]]; then
      pass "Pinned response served for $SESSION_URL"
    else
      fail "Pinned response not detected (header missing)"
    fi

    # Unpin
    curl -sf --max-time 3 -X DELETE "$API/api/pins?url=$(python3 -c "import urllib.parse,sys; print(urllib.parse.quote('$SESSION_URL', safe=''))")" -o /dev/null
    pass "Unpin session"
  else
    fail "Pin request failed"
  fi
else
  fail "No session to pin"
fi

# --- HTTPS tests ---

# 8. CA cert endpoint
CA_CERT=$(curl -sf --max-time 3 -o /tmp/easyntercept-ca.crt "$API/ca" && echo "ok")
if [[ "$CA_CERT" == "ok" ]] && head -1 /tmp/easyntercept-ca.crt | grep -q "BEGIN CERTIFICATE"; then
  pass "CA cert downloadable (/ca endpoint)"
else
  fail "CA cert endpoint broken"
fi

# 9. HTTPS proxy — forward request (relies on CA being installed in system keychain)
HTTPS_BODY=$(curl -sf --max-time 20 -x "$PROXY" https://httpbin.org/get)
if echo "$HTTPS_BODY" | grep -q '"url"'; then
  pass "HTTPS proxy forwards request (httpbin.org/get)"
else
  fail "HTTPS proxy did not return expected response"
fi

# 10. HTTPS session was stored
HTTPS_COUNT=$(curl -sf --max-time 3 "$API/api/sessions" | python3 -c "import sys,json; d=json.load(sys.stdin); print(len([s for s in d if s['url'].startswith('https://')]))")
if [[ $HTTPS_COUNT -gt 0 ]]; then
  pass "HTTPS session stored in memory ($HTTPS_COUNT sessions)"
else
  fail "No HTTPS sessions stored"
fi


HTTPS_BODY=$(curl -sf --max-time 20 -x "$PROXY" https://nos.nl)
if echo "$HTTPS_BODY" | grep -q 'nos'; then
  pass "HTTPS proxy forwards request (nos.nl)"
else
  fail "HTTPS proxy did not return expected response (nos.nl)"
fi

echo ""
echo "=== Results: $PASS passed, $FAIL failed ==="
[[ $FAIL -eq 0 ]]
