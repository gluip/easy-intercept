import type { ProxySession } from "../types";

/** Still in flight: no status yet, or the status is known but the body (or WebSocket) is still open. */
export function isPending(s: ProxySession): boolean {
  return s.responseStatus === 0 || s.responseComplete === false;
}

/** Status known, but bytes are still arriving (streaming response or open WebSocket). */
export function isStreaming(s: ProxySession): boolean {
  return s.responseStatus !== 0 && s.responseComplete === false;
}

/** A WebSocket session carries a message list (possibly empty); ordinary HTTP sessions never do. */
export function isWebSocket(s: ProxySession): boolean {
  return Array.isArray(s.webSocketMessages);
}

/** Elapsed time so far for in-flight sessions, the final duration otherwise. Never negative. */
export function liveDurationMs(s: ProxySession, now: number = Date.now()): number {
  const dur = isPending(s) ? now - new Date(s.timestamp).getTime() : s.durationMs;
  return Math.max(dur, 0);
}
