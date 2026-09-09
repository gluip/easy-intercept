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
 * value is not such a string. Scalars ("5", "true", quoted strings) are not
 * unwrapped — showing them as a tree adds nothing.
 */
export function parseJsonString(val: unknown): unknown {
  if (typeof val !== "string") return undefined;
  const trimmed = val.trimStart();
  if (!trimmed.startsWith("{") && !trimmed.startsWith("[")) return undefined;
  try {
    return JSON.parse(val);
  } catch {
    return undefined;
  }
}
