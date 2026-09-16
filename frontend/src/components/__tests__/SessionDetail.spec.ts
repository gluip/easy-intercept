import { describe, it, expect } from "vitest";
import { createSSRApp, h } from "vue";
import { renderToString } from "@vue/server-renderer";
import type { ProxySession } from "../../types";
import SessionDetail from "../SessionDetail.vue";

function session(overrides: Partial<ProxySession>): ProxySession {
  return {
    id: "s",
    timestamp: "2026-01-01T00:00:00.000Z",
    method: "GET",
    url: "https://example.com/stream",
    requestHeaders: {},
    requestBody: "",
    responseStatus: 200,
    responseHeaders: { "content-type": "text/event-stream" },
    responseBody: "data: hi\n\n",
    durationMs: 42,
    ...overrides,
  };
}

function render(s: ProxySession): Promise<string> {
  return renderToString(createSSRApp({ render: () => h(SessionDetail, { session: s }) }));
}

describe("SessionDetail status line", () => {
  it("says Pending before a status arrives", async () => {
    const html = await render(session({ responseStatus: 0, responseComplete: false, responseBody: "" }));
    expect(html).toContain("Pending…");
    expect(html).not.toContain("streaming…");
  });

  it("says streaming while the body is still arriving, and shows what has arrived", async () => {
    const html = await render(session({ responseComplete: false, responseBody: "data: partial\n\n" }));
    expect(html).toContain("200 · streaming…");
    expect(html).not.toContain("42ms");
    expect(html).toContain("data: partial");
  });

  it("shows the duration once complete", async () => {
    const html = await render(session({ responseComplete: true }));
    expect(html).toContain("200 · 42ms");
  });

  it("treats sessions without the flag as complete", async () => {
    const html = await render(session({}));
    expect(html).toContain("200 · 42ms");
  });
});

describe("SessionDetail WebSocket dispatch", () => {
  it("renders the WebSocket view instead of the generic body for a socket session", async () => {
    const html = await render(session({
      url: "wss://example.com/socket",
      responseStatus: 101,
      responseHeaders: { Upgrade: "websocket" },
      responseBody: "",
      responseComplete: false,
      webSocketMessages: [{ direction: "out", offsetMs: 12, type: "text", data: "ping" }],
    }));
    expect(html).toContain("101 · open");
    expect(html).toContain("→ sent");
    expect(html).toContain("ping");
    expect(html).toContain("Handshake Response Headers");
    expect(html).not.toContain("Response Body");
  });
});
