#!/usr/bin/env zsh
# Sample REST API requests via EasyIntercept proxy
# Usage: ./samples.sh

PROXY="http://localhost:9999"

echo "=== EasyIntercept sample requests ==="
echo ""

# GET — JSON
echo "→ GET json..."
curl -s -x "$PROXY" https://jsonplaceholder.typicode.com/posts/1 | head -5
echo -e "\n"

# GET — list
echo "→ GET list..."
curl -s -x "$PROXY" https://jsonplaceholder.typicode.com/users | head -5
echo ""

# POST — create
echo "→ POST create..."
curl -s -x "$PROXY" -X POST https://jsonplaceholder.typicode.com/posts \
  -H "Content-Type: application/json" \
  -d '{"title":"EasyIntercept test","body":"Hello from proxy","userId":1}' | head -5
echo -e "\n"

# PUT — update
echo "→ PUT update..."
curl -s -x "$PROXY" -X PUT https://jsonplaceholder.typicode.com/posts/1 \
  -H "Content-Type: application/json" \
  -d '{"id":1,"title":"Updated via proxy","body":"Modified","userId":1}' | head -3
echo -e "\n"

# PATCH — partial update
echo "→ PATCH partial..."
curl -s -x "$PROXY" -X PATCH https://jsonplaceholder.typicode.com/posts/1 \
  -H "Content-Type: application/json" \
  -d '{"title":"Patched title"}' | head -3
echo -e "\n"

# DELETE
echo "→ DELETE..."
curl -s -x "$PROXY" -X DELETE https://jsonplaceholder.typicode.com/posts/1 -o /dev/null -w "status: %{http_code}"
echo -e "\n"

# GET — different API (httpbin)
echo "→ GET httpbin headers..."
curl -s -x "$PROXY" https://httpbin.org/headers | head -8
echo -e "\n"

# POST — form data
echo "→ POST form data..."
curl -s -x "$PROXY" -X POST https://httpbin.org/post \
  -d "username=martijn&tool=easyintercept" | grep -A5 '"form"'
echo ""

echo "=== Done — check http://localhost:8080 ==="
