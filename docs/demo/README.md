# Demo recorder

Regenerates [`../demo.gif`](../demo.gif), the animation at the top of the main README.

It drives the **real** UI in a headless Chrome while five requests go through the proxy:
a plain GitHub call, two OpenAI turns of one conversation (tool call → tool result → answer),
an Anthropic call with a thinking block and a tool use, and a Gemini call. All responses are
served by the auto-responder rules in `data/auto-responder/`, so **no API keys and no real
prompts are involved**, and the recording is identical every time.

## Run it

Prerequisites: Node.js, Google Chrome, `curl`, a built frontend (`npx vite build` in `frontend/`),
and no other EasyIntercept instance running (the app is single-instance).

```bash
cd docs/demo
npm install
npm run record
```

The script starts the app itself with `--DataRoot=docs/demo/data` (so it picks up the demo rules and
generates its own CA there), records ~20 s at 5 fps, encodes the GIF, and stops the app again.
If an instance is already listening on port 1337 it is used as-is, after checking that it has the
demo rules loaded.

Knobs (environment variables): `W`/`H` viewport (default 1280×720), `DRAG` extra list-pane width
(default 90), `FPS` (5), `TOTAL` seconds (20.5), `KEYFRAMES=1` to also write a few PNGs for
inspection, `KEEP_APP=1` to leave the app running afterwards.

## How the GIF stays small

Frames share one global 255-colour palette and every frame after the first only stores the pixels
that changed (transparent index + "do not dispose"). 101 frames at 1280×720 come out around 0.4 MB.

## Changing the scenario

- `data/auto-responder/*.json` — the mocked responses (exact-URL match; the two OpenAI turns are
  told apart by the `user` field in the request body).
- `bodies/*.json` — the request bodies sent through the proxy.
- The timeline (what happens when) is the `timeline` array in `record.js`.
