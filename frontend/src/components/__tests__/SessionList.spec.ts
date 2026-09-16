import { describe, it, expect, vi } from "vitest";
import { createSSRApp, h, ref } from "vue";
import { renderToString } from "@vue/server-renderer";
import type { ProxySession } from "../../types";

// SessionList reads column/mark preferences from localStorage at setup and talks to SignalR through
// useProxy; neither exists under server rendering.
const storage = new Map<string, string>();
(globalThis as any).localStorage = {
  getItem: (k: string) => storage.get(k) ?? null,
  setItem: (k: string, v: string) => storage.set(k, v),
  removeItem: (k: string) => storage.delete(k),
};
vi.mock("../../composables/useProxy", () => ({
  useProxy: () => ({ appInfo: ref(null) }),
}));

const SessionList = (await import("../SessionList.vue")).default;

function session(overrides: Partial<ProxySession>): ProxySession {
  return {
    id: "row-" + Math.random().toString(36).slice(2),
    timestamp: new Date().toISOString(),
    method: "GET",
    url: "https://example.com/path",
    requestHeaders: {},
    requestBody: "",
    responseStatus: 200,
    responseHeaders: {},
    responseBody: "ok",
    durationMs: 77,
    ...overrides,
  };
}

function render(sessions: ProxySession[]): Promise<string> {
  return renderToString(createSSRApp({ render: () => h(SessionList, { sessions, selectedIds: [] }) }));
}

function statusCell(html: string): string {
  const m = html.match(/<td[^>]*class="[^"]*col-status[^"]*"[^>]*>/);
  if (!m) throw new Error("no status cell in " + html);
  return m[0];
}

describe("SessionList streaming rows", () => {
  it("pulses the status cell and hides the duration while a response streams", async () => {
    const html = await render([session({ responseComplete: false })]);
    expect(statusCell(html)).toContain("streaming");
    expect(statusCell(html)).toContain("Streaming…");
    expect(html).not.toContain(">77<");
  });

  it("renders a finished row normally", async () => {
    const html = await render([session({ responseComplete: true })]);
    expect(statusCell(html)).not.toContain("streaming");
    expect(html).toContain(">77<");
  });

  it("keeps the pending dots until a status arrives", async () => {
    const html = await render([session({ responseStatus: 0, responseComplete: false })]);
    expect(html).toContain("pending-dots");
    expect(statusCell(html)).not.toContain("streaming");
  });

  it("badges WebSocket sessions", async () => {
    const html = await render([session({ responseStatus: 101, responseComplete: false, webSocketMessages: [] })]);
    expect(html).toContain('class="ws-badge"');
    expect(statusCell(html)).toContain("WebSocket open");
  });
});
