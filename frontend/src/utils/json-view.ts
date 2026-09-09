// Helpers for rendering payloads that carry JSON inside a string.
// LLM tool calls/results almost always do this: the transport value is a string
// holding an encoded JSON document, which renders as escaped noise
// (`"{\"name\":\"…\"}"`) unless it is decoded first.

/**
 * If `val` is a string containing a JSON object or array, return the parsed
 * value. Anything else (plain text, numbers, already-structured data) is
 * returned unchanged.
 */
export function unwrapJsonString(val: unknown): unknown {
  const parsed = parseJsonString(val);
  return parsed === undefined ? val : parsed;
}

/**
 * Parse a string that holds a JSON object or array; returns undefined when the
 * value is not such a string. Success is always an object or array, so the
 * "not JSON" sentinel stays distinguishable from a parse result. Scalars ("5",
 * "true", quoted strings) are not unwrapped — showing them as a tree adds nothing.
 */
export function parseJsonString(val: unknown): object | undefined {
  if (typeof val !== "string") return undefined;
  const trimmed = val.trimStart();
  if (!trimmed.startsWith("{") && !trimmed.startsWith("[")) return undefined;
  try {
    const parsed: unknown = JSON.parse(val);
    // A `{`/`[` document always parses to an object or array; checking keeps
    // the return type honest instead of leaning on that.
    return typeof parsed === "object" && parsed !== null ? parsed : undefined;
  } catch {
    return undefined;
  }
}
