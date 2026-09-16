import { describe, it, expect } from "vitest";
import { createSSRApp, h } from "vue";
import { renderToString } from "@vue/server-renderer";
import type { ProxySession, WebSocketMessage } from "../../types";
import WebSocketSessionDetail from "../WebSocketSessionDetail.vue";

function session(messages: WebSocketMessage[], complete: boolean): ProxySession {
  return {
    id: "ws",
    timestamp: "2026-01-01T00:00:00.000Z",
    method: "GET",
    url: "wss://example.com/socket",
    requestHeaders: { Upgrade: "websocket" },
    requestBody: "",
    responseStatus: 101,
    responseHeaders: { Upgrade: "websocket", "Sec-WebSocket-Accept": "x" },
    responseBody: "",
    durationMs: 1234,
    responseComplete: complete,
    webSocketMessages: messages,
  };
}

function render(s: ProxySession): Promise<string> {
  return renderToString(createSSRApp({ render: () => h(WebSocketSessionDetail, { session: s }) }));
}

describe("WebSocketSessionDetail", () => {
  it("labels directions and counts sent/received", async () => {
    const html = await render(session([
      { direction: "out", offsetMs: 5, type: "text", data: "hello" },
      { direction: "in", offsetMs: 9, type: "text", data: "world" },
      { direction: "in", offsetMs: 12, type: "text", data: "again" },
    ], true));
    expect(html).toContain("→ sent");
    expect(html).toContain("← received");
    expect(html).toContain("1 sent · 2 received");
    expect(html).toContain("0.005s");
  });

  it("shows open/live while the socket is still open and closed afterwards", async () => {
    expect(await render(session([], false))).toContain("open · live");
    expect(await render(session([], true))).toContain("closed");
  });

  it("pretty-prints JSON payloads and leaves other text alone", async () => {
    const html = await render(session([
      { direction: "in", offsetMs: 1, type: "text", data: '{"a":1,"b":[2]}' },
      { direction: "in", offsetMs: 2, type: "text", data: "plain {not json" },
    ], true));
    expect(html).toContain("&quot;a&quot;: 1");
    expect(html).toContain("plain {not json");
  });

  it("renders close and binary entries as given", async () => {
    const html = await render(session([
      { direction: "out", offsetMs: 1, type: "binary", data: "[2048 bytes binary]" },
      { direction: "in", offsetMs: 2, type: "note", data: "[5 more messages not captured]" },
      { direction: "in", offsetMs: 3, type: "close", data: "1000 bye" },
    ], true));
    expect(html).toContain("[2048 bytes binary]");
    expect(html).toContain("[5 more messages not captured]");
    expect(html).toContain("← close");
    expect(html).toContain("1000 bye");
    expect(html).toContain("1 sent · 0 received"); // close/note are not data messages
  });

  it("says so when nothing has been exchanged yet", async () => {
    expect(await render(session([], false))).toContain("No messages yet");
  });
});
