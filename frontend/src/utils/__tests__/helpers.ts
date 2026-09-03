import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { dirname, join } from "node:path";
import type { ProxySession } from "../../types";

const here = dirname(fileURLToPath(import.meta.url));

/** Load a captured proxy session from the fixtures dir.
 *  On-disk session files use PascalCase keys; the HTTP API camelCases them. */
export function loadSession(name: string): ProxySession {
  const raw = JSON.parse(readFileSync(join(here, "fixtures", name), "utf8"));
  return {
    id: raw.Id,
    timestamp: raw.Timestamp,
    method: raw.Method,
    url: raw.Url,
    requestHeaders: raw.RequestHeaders,
    requestBody: raw.RequestBody,
    responseStatus: raw.ResponseStatus,
    responseHeaders: raw.ResponseHeaders,
    responseBody: raw.ResponseBody,
    durationMs: raw.DurationMs,
  };
}
