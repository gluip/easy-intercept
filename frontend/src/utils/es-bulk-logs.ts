const BULK_ACTIONS = ["create", "index", "update", "delete"] as const;

export interface BulkLogEntry {
  action: string;
  timestamp: string | null;
  level: string | null;
  /** Full raw message field from the document */
  message: string;
  /** Clean/short message (e.g. from Serilog wrapper metadata), falls back to first line of message */
  displayMessage: string;
  logger: string | null;
  /** Logger name from wrapper metadata (e.g. SerilogWrapper), if different */
  wrapperLogger: string | null;
  /** App.Class.MethodName from ECS labels */
  source: string | null;
  customData: Record<string, unknown> | null;
  stackTrace: string | null;
  exception: string | null;
  /** Per-item status from the bulk response (e.g. 201) */
  status: number | null;
  /** Per-item error from the bulk response */
  responseError: string | null;
  /** Full parsed document */
  doc: Record<string, unknown> | null;
}

function str(v: unknown): string | null {
  return typeof v === "string" && v.length > 0 ? v : null;
}

function asObj(v: unknown): Record<string, unknown> | null {
  return v && typeof v === "object" && !Array.isArray(v)
    ? (v as Record<string, unknown>)
    : null;
}

/**
 * Cheap check whether a _bulk NDJSON body contains log documents
 * (ECS-style: log.level / message / @timestamp) rather than regular
 * document indexing. Only inspects the first action/document pair.
 */
export function isBulkLogBody(requestBody: string): boolean {
  const lines = (requestBody ?? "").split("\n").map((l) => l.trim()).filter((l) => l.length > 0);
  for (let i = 0; i < lines.length - 1; i++) {
    let actionObj: Record<string, unknown> | null;
    try {
      actionObj = asObj(JSON.parse(lines[i]));
    } catch {
      return false;
    }
    if (!actionObj || !BULK_ACTIONS.some((a) => a in actionObj)) return false;
    if ("delete" in actionObj) continue; // no document line; check next pair
    let doc: Record<string, unknown> | null;
    try {
      doc = asObj(JSON.parse(lines[i + 1]));
    } catch {
      return false;
    }
    if (!doc) return false;
    const log = asObj(doc["log"]);
    const level = str(doc["log.level"]) ?? str(log?.["level"]);
    return level !== null || (str(doc["message"]) !== null && str(doc["@timestamp"]) !== null);
  }
  return false;
}

export function levelClass(level: string | null): string {
  const l = (level ?? "").toLowerCase();
  if (l.startsWith("err") || l.startsWith("fatal") || l.startsWith("crit")) return "error";
  if (l.startsWith("warn")) return "warning";
  if (l.startsWith("debug") || l.startsWith("verbose") || l.startsWith("trace")) return "debug";
  if (l.startsWith("info")) return "info";
  return "none";
}

/**
 * Parse an Elasticsearch _bulk NDJSON request body into log entries,
 * aligning each entry with its per-item status from the bulk response.
 * Returns an empty array when the documents don't look like log entries
 * (e.g. regular document indexing), so callers can fall back to a raw view.
 */
export function parseBulkLogEntries(requestBody: string, responseBody: string): BulkLogEntry[] {
  const entries: BulkLogEntry[] = [];

  let responseItems: unknown[] = [];
  try {
    const resp = JSON.parse(responseBody);
    if (Array.isArray(resp?.items)) responseItems = resp.items;
  } catch {
    // no per-item statuses available
  }

  const lines = (requestBody ?? "")
    .split("\n")
    .map((l) => l.trim())
    .filter((l) => l.length > 0);

  let i = 0;
  while (i < lines.length) {
    let actionObj: Record<string, unknown> | null;
    try {
      actionObj = asObj(JSON.parse(lines[i]));
    } catch {
      i++;
      continue;
    }
    const action = BULK_ACTIONS.find((a) => actionObj && a in actionObj);
    if (!action) {
      i++;
      continue;
    }
    i++;

    let doc: Record<string, unknown> | null = null;
    if (action !== "delete" && i < lines.length) {
      try {
        doc = asObj(JSON.parse(lines[i]));
      } catch {
        doc = null;
      }
      i++;
    }

    const log = asObj(doc?.["log"]);
    const level = str(doc?.["log.level"]) ?? str(log?.["level"]);
    const logger = str(log?.["logger"]) ?? str(doc?.["log.logger"]);
    const message = str(doc?.["message"]) ?? "";

    // Serilog wrappers often embed a structured event object in metadata
    // (name comes from the message template, e.g. {@Info}) holding the
    // clean message, exception and stack trace.
    const metadata = asObj(doc?.["metadata"]);
    let shortMessage: string | null = null;
    let stackTrace: string | null = null;
    let exception: string | null = null;
    let wrapperLogger: string | null = null;
    if (metadata) {
      for (const v of Object.values(metadata)) {
        const o = asObj(v);
        if (o && ("StackTrace" in o || "Message" in o || "Exception" in o)) {
          shortMessage = str(o["Message"]);
          stackTrace = str(o["StackTrace"]);
          const ex = o["Exception"];
          exception =
            ex == null ? null : typeof ex === "string" ? ex : JSON.stringify(ex, null, 2);
          wrapperLogger = str(o["Logger"]);
          break;
        }
      }
    }

    // ECS standard error field
    const err = asObj(doc?.["error"]);
    if (err) {
      exception = exception ?? str(err["message"]) ?? str(err["type"]);
      stackTrace = stackTrace ?? str(err["stack_trace"]);
    }

    const labels = asObj(doc?.["labels"]);
    const source = labels
      ? [str(labels["App"]), str(labels["Class"]), str(labels["MethodName"])]
          .filter(Boolean)
          .join(".") || null
      : null;

    const customData =
      asObj(metadata?.["custom"]) ?? asObj(metadata?.["customData"]) ?? null;

    // Per-item response status/error (bulk response items align by order)
    let status: number | null = null;
    let responseError: string | null = null;
    const item = asObj(responseItems[entries.length]);
    if (item) {
      const result = asObj(item[action]) ?? asObj(Object.values(item)[0]);
      if (result) {
        status = typeof result["status"] === "number" ? (result["status"] as number) : null;
        const respErr = result["error"];
        if (respErr != null) {
          const eo = asObj(respErr);
          responseError = str(eo?.["reason"]) ?? JSON.stringify(respErr);
        }
      }
    }

    entries.push({
      action,
      timestamp: str(doc?.["@timestamp"]),
      level,
      message,
      displayMessage: shortMessage ?? message.split("\n")[0],
      logger,
      wrapperLogger,
      source,
      customData,
      stackTrace,
      exception,
      status,
      responseError,
      doc,
    });
  }

  // Only treat the bulk as a log batch when every document carries log
  // fields (level or message); document-indexing bulks get the raw view.
  if (!entries.length || !entries.every((e) => e.level !== null || e.message.length > 0)) {
    return [];
  }

  return entries;
}
