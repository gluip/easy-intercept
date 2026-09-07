// Records docs/demo.gif. See README.md in this folder.
//
// Flow: (start app with the demo data root) → clear sessions → open UI in headless Chrome →
// replay a timeline of mocked requests + UI clicks while grabbing screenshots → encode GIF.
const { chromium } = require("playwright-core");
const { PNG } = require("pngjs");
const { GIFEncoder, quantize, applyPalette, nearestColorIndex } = require("gifenc");
const { spawn, execFileSync } = require("child_process");
const fs = require("fs");
const path = require("path");

const REPO = path.resolve(__dirname, "..", "..");
const DATA_ROOT = path.join(__dirname, "data");
const BODIES = path.join(__dirname, "bodies");
const OUT = path.resolve(__dirname, "..", "demo.gif");
const UI = "http://localhost:1337";
const PROXY = "http://127.0.0.1:9999";

const W = +(process.env.W || 1280), H = +(process.env.H || 720);
const DRAG = +(process.env.DRAG || 90), FPS = +(process.env.FPS || 5), TOTAL = +(process.env.TOTAL || 24);
const KEYFRAMES = !!process.env.KEYFRAMES, KEEP_APP = !!process.env.KEEP_APP;

const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
const fail = (msg) => { console.error("\n" + msg); process.exit(1); };

async function isUp() {
  try { return (await fetch(`${UI}/api/info`)).ok; } catch { return false; }
}

// ---- app lifecycle ---------------------------------------------------------------------------

let app = null;

async function ensureApp() {
  if (await isUp()) { console.log("using the EasyIntercept instance already on port 1337"); return; }
  if (!fs.existsSync(path.join(REPO, "EasyIntercept", "wwwroot", "index.html")))
    fail("frontend not built: run `npx vite build --emptyOutDir` in frontend/ first");

  console.log("starting EasyIntercept with --DataRoot=" + DATA_ROOT);
  app = spawn("dotnet",
    ["run", "--project", path.join(REPO, "EasyIntercept"), "--", "--no-browser", "--no-tray", `--DataRoot=${DATA_ROOT}`],
    { stdio: "ignore", detached: process.platform !== "win32" });
  for (let i = 0; i < 180; i++) { if (await isUp()) return; await sleep(500); }
  fail("the app did not come up on port 1337 within 90 s.\n" +
       "If another EasyIntercept is running, stop it first: the app is single-instance.");
}

function stopApp() {
  if (!app || KEEP_APP) return;
  if (process.platform === "win32") {
    // dotnet run spawns EasyIntercept.exe as a child; kill the whole tree.
    try { execFileSync("taskkill", ["/pid", String(app.pid), "/T", "/F"], { stdio: "ignore" }); } catch {}
  } else {
    try { process.kill(-app.pid, "SIGTERM"); } catch {}
  }
}

// The demo rules in data/auto-responder share this id prefix, so they can be told apart
// from whatever else a running instance may have loaded.
const DEMO_RULE_ID_PREFIX = "11111111-1111-4111-8111-1111111111";

async function checkDemoRules() {
  const rules = await (await fetch(`${UI}/api/auto-responders`)).json();
  const demo = rules.filter((r) => r.id.startsWith(DEMO_RULE_ID_PREFIX) && r.isEnabled).length;
  if (demo < 5)
    fail(`expected the 5 enabled demo auto-responder rules, found ${demo}.\n` +
         `The running instance is probably using a different DataRoot than ${DATA_ROOT}.`);
}

// ---- traffic ---------------------------------------------------------------------------------

const CA = path.join(DATA_ROOT, "certs", "easyntercept-ca.crt");
const json = (name) => ["-H", "Content-Type: application/json", "--data-binary", "@" + path.join(BODIES, name)];
const REQUESTS = {
  github:    ["-H", "Accept: application/vnd.github+json", "https://api.github.com/repos/gluip/easy-intercept"],
  openai1:   [...json("openai1.json"),   "-H", "Authorization: Bearer sk-proj-demo", "https://api.openai.com/v1/chat/completions"],
  openai2:   [...json("openai2.json"),   "-H", "Authorization: Bearer sk-proj-demo", "https://api.openai.com/v1/chat/completions"],
  anthropic: [...json("anthropic.json"), "-H", "x-api-key: sk-ant-demo", "-H", "anthropic-version: 2023-06-01", "https://api.anthropic.com/v1/messages"],
  gemini:    [...json("gemini.json"),    "https://generativelanguage.googleapis.com/v1beta/models/gemini-2.5-flash:generateContent"],
};
function send(name) {
  // --ssl-no-revoke only matters for Schannel builds of curl (Windows); it is accepted elsewhere.
  spawn("curl", ["-s", "-o", process.platform === "win32" ? "NUL" : "/dev/null", "--max-time", "15",
    "-x", PROXY, "--cacert", CA, "--ssl-no-revoke", ...REQUESTS[name]], { stdio: "ignore" });
}

// ---- recording -------------------------------------------------------------------------------

async function record() {
  await fetch(`${UI}/api/sessions`, { method: "DELETE" });

  const browser = await chromium.launch({ channel: "chrome", headless: true });
  const page = await browser.newPage({ viewport: { width: W, height: H }, deviceScaleFactor: 1 });
  await page.goto(UI + "/");
  await page.waitForTimeout(1200);

  // widen the list pane so the LLM-only columns fit
  const box = await page.locator(".divider").boundingBox();
  const cx = box.x + box.width / 2, cy = box.y + 200;
  await page.mouse.move(cx, cy); await page.mouse.down();
  await page.mouse.move(cx + DRAG, cy, { steps: 6 }); await page.mouse.up();
  await page.waitForTimeout(400);

  const timelineToggle = page.getByRole("checkbox", { name: /Timeline/ });
  const timeline = [
    // requests arrive; the waterfall goes on while some are still in flight so its bars grow live
    [0.8,  () => send("github")],
    [2.2,  () => send("openai1")],
    [3.6,  () => timelineToggle.check()],
    [4.0,  () => send("anthropic")],               // 1.4 s mock latency
    [5.6,  () => send("gemini")],
    [7.0,  () => send("openai2")],                 // 2.1 s mock latency: pending state + growing bar
    [10.6, () => timelineToggle.uncheck()],
    // LLM-only columns and the chat-transcript view
    [11.2, () => page.getByRole("checkbox", { name: "LLM requests only" }).check()],
    [12.4, () => page.locator("tr[data-id]", { hasText: "api.anthropic.com" }).first().click()],
    [15.6, () => page.locator("tr[data-id]", { hasText: "The 502" }).first().click()],
    // turn the captured response into a mock rule (form only; nothing is saved)
    [18.2, () => page.getByRole("button", { name: /Add to Auto Responder/ }).click()],
    [20.6, () => page.getByRole("button", { name: /Format/ }).click()],
  ];

  const frames = [];
  const t0 = Date.now();
  let next = 0;
  for (;;) {
    const t = (Date.now() - t0) / 1000;
    while (next < timeline.length && t >= timeline[next][0]) { await timeline[next][1](); next++; }
    if (t > TOTAL) break;
    const png = await page.screenshot({ type: "png" });
    frames.push({ png, t: (Date.now() - t0) / 1000 });
    const wait = 1000 / FPS - ((Date.now() - t0) / 1000 - t) * 1000;
    if (wait > 0) await sleep(wait);
  }
  await browser.close();
  console.log(`captured ${frames.length} frames over ${frames.at(-1).t.toFixed(1)} s`);

  if (KEYFRAMES) {
    const keyAt = (sec) => frames.reduce((b, f) => (Math.abs(f.t - sec) < Math.abs(b.t - sec) ? f : b));
    for (const sec of [8.5, 11.8, 14, 17, 19.5, 23]) fs.writeFileSync(path.join(__dirname, `key-${sec}s.png`), keyAt(sec).png);
  }
  return frames;
}

// ---- encoding --------------------------------------------------------------------------------

function encode(frames) {
  const decoded = frames.map((f) => PNG.sync.read(f.png).data);
  const N = decoded.length;
  const sampleIdx = [0, Math.floor(N * 0.5), Math.floor(N * 0.7), Math.floor(N * 0.9), N - 1];
  const base = quantize(Buffer.concat(sampleIdx.map((i) => decoded[i])), 255, { format: "rgb565" });
  const palette = [[255, 0, 255], ...base]; // index 0 = "unchanged since previous frame"

  const gif = GIFEncoder();
  const px = W * H;
  let prev = null;
  for (let i = 0; i < N; i++) {
    const rgba = decoded[i];
    const index = applyPalette(rgba, palette, "rgb565");
    for (let p = 0; p < px; p++) {
      const o = p * 4;
      if (prev && rgba[o] === prev[o] && rgba[o + 1] === prev[o + 1] && rgba[o + 2] === prev[o + 2]) index[p] = 0;
      else if (index[p] === 0) index[p] = nearestColorIndex(base, [rgba[o], rgba[o + 1], rgba[o + 2]]) + 1;
    }
    const delay = i < N - 1 ? Math.round((frames[i + 1].t - frames[i].t) * 1000) : 2500;
    gif.writeFrame(index, W, H, { palette, delay, repeat: 0, transparent: prev !== null, transparentIndex: 0, dispose: 1 });
    prev = rgba;
  }
  gif.finish();
  return gif.bytes();
}

// ---- main ------------------------------------------------------------------------------------

(async () => {
  try {
    await ensureApp();
    await checkDemoRules();
    if (!fs.existsSync(CA)) fail(`CA not found at ${CA}; the app should have generated it on start.`);
    const frames = await record();
    const bytes = encode(frames);
    fs.writeFileSync(OUT, bytes);
    console.log(`wrote ${OUT}: ${(bytes.length / 1048576).toFixed(2)} MB, ${frames.length} frames, ${W}x${H}`);
  } finally {
    stopApp();
  }
})().catch((e) => { console.error(e); stopApp(); process.exit(1); });
