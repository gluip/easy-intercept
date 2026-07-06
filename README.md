# EasyIntercept

An HTTP/HTTPS debugging proxy built for the age of LLM APIs. Capture, inspect, mock, and diff traffic through a single lightweight app — with native understanding of OpenAI, Anthropic, Gemini, and GitHub Copilot traffic baked in. Runs great on both **Windows** and **macOS**.

## Why EasyIntercept over Fiddler

Fiddler is a fine general-purpose proxy, but it was designed before "debugging an LLM API call" was a daily task. EasyIntercept is free, open source (MIT), and ships as one self-contained process (proxy + web UI + REST API), and it treats streaming SSE chat completions, tool calls, and token/cost accounting as first-class data instead of opaque JSON blobs.

It's open source. If something here isn't better than Fiddler for your workflow, that's a bug — open an issue or send a PR.

## Feature highlights

### LLM superpowers
- **Zero-config provider detection** — automatically recognizes OpenAI, Anthropic, Google Gemini, and GitHub Copilot traffic just from the request URL.
- **Streaming reconstruction** — reassembles SSE/streamed responses (including fragmented tool-call arguments and Anthropic "thinking" blocks) so streamed and non-streamed traffic look identical in the UI.
- **Built-in cost & token accounting** — per-model pricing tables for every major provider, computed client-side from the intercepted token usage. No extra API calls, no external service.
- **Chat transcript view** — normalizes all four providers into one readable conversation: token pills (prompt/cached/thinking/response/cost), collapsible tool-call and tool-result blocks, and a schema panel for declared tools. Falls back to the raw payload if anything fails to parse.
- **Timeline mode** — a waterfall-style view of request start time and duration, live-updating for in-flight requests. Great for seeing how a multi-step agent loop actually overlaps or sequences its calls.
- **Session list superpowers for LLM traffic** — an "LLM only" filter with dedicated Tools/Results/Cost columns, an inline chat preview right in the list, and automatic color-grouping of requests that belong to the same multi-turn conversation.
- **Compare view** — select any two sessions and get a true side-by-side diff (headers and body, both directions) with JSON/XML pretty-printing — ideal for comparing prompt variations or two runs of the same agent step.

Bonus: GraphQL and Elasticsearch traffic also get their own smart detail viewers.

### Core proxy features
- **Works great on Windows and macOS** — first-class support on both, not just a Windows-first port: CA install scripts, the system-proxy toggle, and the dev build/run scripts (`restart.ps1` / `restart.sh`) all have a native counterpart on each OS.
- **HTTPS interception** via a locally-generated root CA and on-the-fly per-host certificates — plus a QR-code mobile install page (`/install`) that's noticeably friendlier than Fiddler's desktop-centric certificate flow.
- **One-click system proxy toggle** that actually flips the OS-level proxy setting (Windows registry / macOS `networksetup`), not just an in-app flag.
- **Auto Responder** — turn any captured response into a mock in one click, match on method/URL/body, inject artificial latency, and edit rules as plain JSON files that hot-reload into the running proxy the moment you save them.
- **Session replay** that round-trips back through EasyIntercept itself, so replayed requests are just as interceptable and mockable as the original traffic.
- **Persistent sessions** — every request is saved to disk as its own JSON file (easy to grep, share, or version-control) in addition to an in-memory index for a snappy UI, and everything updates live over SignalR as traffic happens.

## Vs. Fiddler, briefly

| | EasyIntercept | Fiddler |
|---|---|---|
| LLM-aware traffic parsing (OpenAI/Anthropic/Gemini/Copilot) | ✅ built-in | ❌ |
| Token usage & cost calculator | ✅ built-in | ❌ |
| Streaming SSE reconstruction | ✅ built-in | ❌ |
| Side-by-side session diff/compare | ✅ built-in | ❌ |
| Mock rules as version-controllable JSON files with hot reload | ✅ | partial |
| Mobile CA install via QR code | ✅ | ❌ |
| License | Free & open source (MIT) | Free tier + paid tiers |

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

## Contributing

PRs welcome — whether it's closing one of the gaps above or adding support for another API provider. This project is MIT-licensed specifically so you can take it, change it, and make it better.

## License

MIT — see [LICENSE](LICENSE).
