# EasyIntercept

A local HTTP/HTTPS debugging proxy built for the age of coding agents. When you're working with Cursor, GitHub Copilot, Claude Code, Windsurf and friends on code that talks to some API, they debug fastest when they can see the real traffic themselves, instead of relying on your description of it. EasyIntercept writes every captured request/response as a plain JSON file in a local `sessions/` folder, so your agent can read or grep the exact request and response directly — no copy-pasting payloads into a chat window. On top of that, it captures, inspects, mocks, and diffs traffic through a single lightweight app, with native understanding of OpenAI, Anthropic, Gemini, and GitHub Copilot requests baked in for when the API in question is an LLM provider.

It's free, open source (MIT), and runs great on both **Windows** and **macOS**. If something isn't working the way you'd like, that's a PR waiting to happen.

## Feature highlights

### 🤖 Agent-friendly
- **Agent-accessible session files** — every request/response is saved to disk as its own plain JSON file in `sessions/` (in addition to an in-memory index for a snappy UI), so a coding agent with filesystem access can read or grep the exact traffic directly — no manual copy-pasting of headers/bodies into a prompt.
- **Auto Responder as mock files** — mock rules are plain JSON files too, matched on method/URL/body with optional latency injection, and hot-reload into the running proxy the moment they change on disk. Turn a captured response into a mock in one click yourself, or let your agent write/edit a rule file directly to stub out an API while it iterates on your code — no UI required either way.
- **Live updates** — the session list updates in real time over SignalR as traffic happens, so both you and your agent are always looking at current state.

### 🧠 Debugging LLM requests
- **Zero-config provider detection** — automatically recognizes OpenAI, Anthropic, Google Gemini, and GitHub Copilot traffic just from the request URL.
- **Streaming reconstruction** — reassembles SSE/streamed responses (including fragmented tool-call arguments and Anthropic "thinking" blocks) so streamed and non-streamed traffic look identical in the UI.
- **Built-in cost & token accounting** — per-model pricing tables for every major provider, computed client-side from the intercepted token usage. No extra API calls, no external service.
- **Chat transcript view** — normalizes all four providers into one readable conversation: token pills (prompt/cached/thinking/response/cost), collapsible tool-call and tool-result blocks, and a schema panel for declared tools. Falls back to the raw payload if anything fails to parse.
- **Session list superpowers for LLM traffic** — an "LLM only" filter with dedicated Tools/Results/Cost columns, an inline chat preview right in the list, and automatic color-grouping of requests that belong to the same multi-turn conversation.

Bonus: GraphQL and Elasticsearch traffic also get their own smart detail viewers.

### 🛠️ General session tools
- **Timeline mode** — a waterfall-style view of request start time and duration, live-updating for in-flight requests. Great for seeing how a burst of requests actually overlaps or sequences.
- **Compare view** — select any two sessions and get a true side-by-side diff (headers and body, both directions) with JSON/XML pretty-printing.
- **Copy, mark, and organize** — copy a request's URL or its on-disk file path, tag sessions with colored marks, filter by request kind (document/asset/API/backend), and multi-select with keyboard navigation for bulk delete.
- **Right-click actions** — compare, show the session file in Explorer, or delete, all from the session list's context menu.

### Also in the box
- **Works great on Windows and macOS** — first-class support on both, not just a Windows-first port: CA install scripts, the system-proxy toggle, and the dev build/run scripts (`restart.ps1` / `restart.sh`) all have a native counterpart on each OS.
- **HTTPS interception** via a locally-generated root CA and on-the-fly per-host certificates — plus a QR-code mobile install page (`/install`) that's noticeably friendlier than Fiddler's desktop-centric certificate flow.
- **One-click system proxy toggle** that actually flips the OS-level proxy setting (Windows registry / macOS `networksetup`), not just an in-app flag.
- **Session replay** (also right-click on a session) that round-trips back through EasyIntercept itself, so replayed requests are just as interceptable and mockable as the original traffic.

## Getting started

**Prerequisites:** .NET 10 SDK, Node.js (for building the frontend).

```bash
# Build the frontend
cd frontend
npm install
npx vite build --emptyOutDir

# Run the backend (serves the UI, REST API, and proxy)
cd ../EasyIntercept
dotnet run
```

- Web UI: [http://localhost:8080](http://localhost:8080)
- Proxy listens on port `9999`

On Windows/macOS, `restart.ps1` / `restart.sh` do the same build-and-run in one step.

### Installing the CA certificate (for HTTPS interception)
- **Desktop:** run `install-ca.ps1` (Windows) or `install-ca.sh` (macOS), or download the cert directly from `http://localhost:8080/ca`.
- **Mobile:** open `http://localhost:8080/install` on your phone (or scan the QR code it shows) for step-by-step iOS install instructions.

Then point your device or app at `<host>:9999` as its HTTP/HTTPS proxy.

## Known limitations

Being upfront about where EasyIntercept isn't there yet — these are also good first contributions:
- Auto Responder rules match on method + exact URL + an optional body predicate — no host-wildcard, path-prefix, or header matching yet, and no "modify a real passthrough response" transform.
- No certificate-pinning bypass (apps that pin certificates won't be interceptable, same as Fiddler without extra tooling).
- First-class OS integration (system-proxy toggle, CA install script, "Show in Explorer") currently covers Windows and macOS; Linux can run the proxy and UI but doesn't get these conveniences yet.
- Session history is capped at 1000 entries (oldest are evicted), not unlimited retention.

## How it compares to Fiddler

| | EasyIntercept | Fiddler |
|---|---|---|
| LLM-aware traffic parsing (OpenAI/Anthropic/Gemini/Copilot) | ✅ built-in | ❌ |
| Token usage & cost calculator | ✅ built-in | ❌ |
| Streaming SSE reconstruction | ✅ built-in | ❌ |
| Side-by-side session diff/compare | ✅ built-in | ❌ |
| Mock rules as version-controllable JSON files with hot reload | ✅ | partial |
| Mobile CA install via QR code | ✅ | ❌ |
| License | Free & open source (MIT) | Free tier + paid tiers |

## Contributing

PRs welcome — whether it's closing one of the gaps above or adding support for another API provider. This project is MIT-licensed specifically so you can take it, change it, and make it better.

## License

MIT — see [LICENSE](LICENSE).
