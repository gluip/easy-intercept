import { describe, it, expect } from "vitest";
import type { ProxySession } from "../../types";
import { isPending, isStreaming, isWebSocket, liveDurationMs } from "../session-state";

function session(overrides: Partial<ProxySession>): ProxySession {
  return {
    id: "s",
    timestamp: "2026-01-01T00:00:00.000Z",
    method: "GET",
    url: "https://example.com/x",
    requestHeaders: {},
    requestBody: "",
    responseStatus: 200,
    responseHeaders: {},
    responseBody: "",
    durationMs: 42,
    ...overrides,
  };
}

describe("isPending", () => {
  it("is pending while no status has arrived", () => {
    expect(isPending(session({ responseStatus: 0, responseComplete: false }))).toBe(true);
    expect(isPending(session({ responseStatus: 0 }))).toBe(true);
  });

  it("is pending while a known status is still streaming its body", () => {
    expect(isPending(session({ responseStatus: 200, responseComplete: false }))).toBe(true);
  });

  it("is not pending once complete, including sessions from before the flag existed", () => {
    expect(isPending(session({ responseStatus: 200, responseComplete: true }))).toBe(false);
    expect(isPending(session({ responseStatus: 200 }))).toBe(false);
    expect(isPending(session({ responseStatus: 502 }))).toBe(false);
  });
});

describe("isStreaming", () => {
  it("needs both a status and an incomplete body", () => {
    expect(isStreaming(session({ responseStatus: 0, responseComplete: false }))).toBe(false);
    expect(isStreaming(session({ responseStatus: 200, responseComplete: false }))).toBe(true);
    expect(isStreaming(session({ responseStatus: 200 }))).toBe(false);
  });
});

describe("isWebSocket", () => {
  it("recognises a session by its message list, even when empty", () => {
    expect(isWebSocket(session({ responseStatus: 101, webSocketMessages: [] }))).toBe(true);
    expect(isWebSocket(session({ responseStatus: 101 }))).toBe(false);
    expect(isWebSocket(session({}))).toBe(false);
  });
});

describe("liveDurationMs", () => {
  const start = Date.parse("2026-01-01T00:00:00.000Z");

  it("ticks with the clock while pending", () => {
    expect(liveDurationMs(session({ responseStatus: 0 }), start + 1500)).toBe(1500);
    expect(liveDurationMs(session({ responseStatus: 200, responseComplete: false }), start + 700)).toBe(700);
  });

  it("uses the final duration once complete", () => {
    expect(liveDurationMs(session({ durationMs: 42 }), start + 99_999)).toBe(42);
  });

  it("never goes negative when clocks disagree", () => {
    expect(liveDurationMs(session({ responseStatus: 0 }), start - 10)).toBe(0);
  });
});
