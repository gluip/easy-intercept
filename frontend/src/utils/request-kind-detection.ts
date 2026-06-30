import type { ProxySession } from "../types";

export type RequestKind = "document" | "asset" | "browser-api" | "backend";

function getHeader(headers: Record<string, string>, name: string): string | undefined {
  const key = Object.keys(headers).find((k) => k.toLowerCase() === name);
  return key ? headers[key] : undefined;
}

export function detectRequestKind(session: ProxySession): RequestKind {
  const dest = getHeader(session.requestHeaders, "sec-fetch-dest")?.toLowerCase();
  if (dest === "document" || dest === "iframe" || dest === "frame") return "document";
  if (dest && ["style", "script", "image", "font", "audio", "video", "track"].includes(dest)) return "asset";
  if (dest) return "browser-api"; // dest === "empty" → fetch()/XHR vanuit paginacode
  if (getHeader(session.requestHeaders, "sec-fetch-mode")) return "browser-api"; // oudere Fetch Metadata zonder dest
  return "backend"; // geen Sec-Fetch-* headers → vermoedelijk niet-browser client
}

export const REQUEST_KIND_LABELS: Record<RequestKind, string> = {
  document: "Document",
  asset: "Asset",
  "browser-api": "Browser API",
  backend: "Backend",
};

export const REQUEST_KIND_ICONS: Record<RequestKind, string> = {
  document: "📄",
  asset: "🎨",
  "browser-api": "🌐",
  backend: "⚙",
};
