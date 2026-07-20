import type { ProxySession } from "../types";
import { isBulkLogBody } from "./es-bulk-logs";

export type ESOperationType = "search" | "pit-create" | "pit-delete" | "bulk" | "bulk-log" | "other";

export function isElasticsearchRequest(session: ProxySession): boolean {
  const url = session.url.toLowerCase();
  return (
    url.includes("elastic-cloud.com") ||
    url.includes(".aws.cloud.es.io") ||
    url.includes("elasticsearch") ||
    (url.includes("/_search") && session.requestBody?.trimStart().startsWith("{")) ||
    url.includes("/_pit") ||
    url.includes("/_bulk") ||
    url.includes("/_msearch")
  );
}

export function detectESOperation(session: ProxySession): ESOperationType {
  const url = session.url.toLowerCase();
  if (url.includes("/_bulk")) return isBulkLogBody(session.requestBody) ? "bulk-log" : "bulk";
  if (url.includes("/_pit")) return session.method === "DELETE" ? "pit-delete" : "pit-create";
  if (url.includes("/_search") || url.includes("/_msearch")) return "search";
  return "other";
}

export function parseESIndex(url: string): string {
  try {
    const path = new URL(url).pathname;
    const parts = path.split("/").filter(Boolean);
    const opIdx = parts.findIndex((p) => p.startsWith("_"));
    if (opIdx > 0) return parts.slice(0, opIdx).join("/");
    if (parts.length > 0 && !parts[0].startsWith("_")) return parts[0];
    return "*";
  } catch {
    return "?";
  }
}

export interface ESFilter {
  type: string;
  field: string;
  value: string;
}

export function parseFilters(query: Record<string, unknown>): ESFilter[] {
  const filters: ESFilter[] = [];

  function walk(obj: unknown) {
    if (!obj || typeof obj !== "object") return;

    const rec = obj as Record<string, unknown>;

    if (rec["term"]) {
      const t = rec["term"] as Record<string, unknown>;
      for (const [field, val] of Object.entries(t)) {
        const v = typeof val === "object" && val !== null && "value" in val
          ? String((val as Record<string, unknown>)["value"])
          : String(val);
        filters.push({ type: "term", field, value: v });
      }
    }

    if (rec["terms"]) {
      const t = rec["terms"] as Record<string, unknown>;
      for (const [field, vals] of Object.entries(t)) {
        const arr = Array.isArray(vals) ? vals : [];
        const preview = arr.slice(0, 3).join(", ") + (arr.length > 3 ? ` +${arr.length - 3}` : "");
        filters.push({ type: "terms", field, value: preview });
      }
    }

    if (rec["range"]) {
      const t = rec["range"] as Record<string, unknown>;
      for (const [field, bounds] of Object.entries(t)) {
        const b = bounds as Record<string, unknown>;
        const parts = [];
        if (b["gte"] !== undefined) parts.push(`>= ${b["gte"]}`);
        if (b["lte"] !== undefined) parts.push(`<= ${b["lte"]}`);
        if (b["gt"] !== undefined) parts.push(`> ${b["gt"]}`);
        if (b["lt"] !== undefined) parts.push(`< ${b["lt"]}`);
        filters.push({ type: "range", field, value: parts.join(" AND ") });
      }
    }

    if (rec["match"] || rec["match_phrase"]) {
      const t = (rec["match"] || rec["match_phrase"]) as Record<string, unknown>;
      for (const [field, val] of Object.entries(t)) {
        const v = typeof val === "object" && val !== null && "query" in val
          ? String((val as Record<string, unknown>)["query"])
          : String(val);
        filters.push({ type: "match", field, value: v });
      }
    }

    if (rec["semantic"]) {
      const t = rec["semantic"] as Record<string, unknown>;
      filters.push({ type: "semantic", field: String(t["field"] ?? ""), value: String(t["query"] ?? "") });
    }

    if (rec["knn"]) {
      const t = rec["knn"] as Record<string, unknown>;
      filters.push({ type: "knn", field: String(t["field"] ?? ""), value: `k=${t["k"] ?? "?"}` });
    }

    if (rec["bool"]) {
      const b = rec["bool"] as Record<string, unknown>;
      for (const key of ["filter", "must", "should", "must_not"]) {
        const clauses = b[key];
        if (Array.isArray(clauses)) clauses.forEach(walk);
        else if (clauses) walk(clauses);
      }
    }
  }

  walk(query);
  return filters;
}
