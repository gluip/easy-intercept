import type { ProxySession } from "../types";

export type GraphQLOperationType = "query" | "mutation" | "subscription" | "unknown";

export interface GraphQLOperation {
  operationName?: string;
  query: string;
  variables?: Record<string, unknown>;
}

export interface GraphQLResult {
  data?: unknown;
  errors?: GraphQLError[];
  extensions?: Record<string, unknown>;
}

export interface GraphQLError {
  message: string;
  path?: (string | number)[];
  extensions?: unknown;
}

function tryParseJson(body: string): unknown | null {
  try {
    return JSON.parse(body);
  } catch {
    return null;
  }
}

function isOperationLike(item: unknown): item is GraphQLOperation {
  return (
    typeof item === "object" &&
    item !== null &&
    typeof (item as Record<string, unknown>).query === "string"
  );
}

/** Parses the request body into one or more GraphQL operations (handles batched array requests). */
export function parseGraphQLRequest(session: ProxySession): GraphQLOperation[] | null {
  const body = tryParseJson(session.requestBody);
  if (body === null) return null;
  const items = Array.isArray(body) ? body : [body];
  if (items.length === 0 || !items.every(isOperationLike)) return null;
  return items as GraphQLOperation[];
}

/** Parses the response body into one or more GraphQL results, aligned by index with the request operations. */
export function parseGraphQLResponse(session: ProxySession): GraphQLResult[] | null {
  const body = tryParseJson(session.responseBody);
  if (body === null) return null;
  const items = Array.isArray(body) ? body : [body];
  return items.map((item) => {
    const rec = (item ?? {}) as Record<string, unknown>;
    return {
      data: rec.data,
      errors: rec.errors as GraphQLError[] | undefined,
      extensions: rec.extensions as Record<string, unknown> | undefined,
    };
  });
}

/** Extracts the Apollo tracing extension's server-side execution duration, converted from nanoseconds to milliseconds. */
export function getTracingDurationMs(result: GraphQLResult | null | undefined): number | null {
  const tracing = result?.extensions?.tracing as { duration?: unknown } | undefined;
  const ns = tracing?.duration;
  return typeof ns === "number" ? ns / 1e6 : null;
}

export function isGraphQLRequest(session: ProxySession): boolean {
  if (parseGraphQLRequest(session)) return true;
  return session.url.toLowerCase().includes("/graphql");
}

export function getOperationType(query: string): GraphQLOperationType {
  const trimmed = query.trimStart();
  if (trimmed.startsWith("mutation")) return "mutation";
  if (trimmed.startsWith("subscription")) return "subscription";
  if (trimmed.startsWith("query") || trimmed.startsWith("{")) return "query";
  return "unknown";
}

/** Falls back to extracting the name from the query text (e.g. "query AccommodationReservation {") when operationName is absent. */
export function getOperationName(op: GraphQLOperation): string | null {
  if (op.operationName) return op.operationName;
  const match = op.query.match(/^\s*(?:query|mutation|subscription)\s+(\w+)/);
  return match ? match[1] : null;
}

const HIGHLIGHT_PATTERN =
  /(#.*$)|("(?:[^"\\]|\\.)*")|(\$[A-Za-z_]\w*)|(@[A-Za-z_]\w*)|(\.\.\.[A-Za-z_]\w*)|\b(query|mutation|subscription|fragment|on|true|false|null)\b/gm;

/** Lightweight syntax highlighting for GraphQL query text (comments, strings, $vars, @directives, ...fragments, keywords). */
export function highlightGraphQL(query: string): string {
  const escaped = query.replace(/[&<>]/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;" }[c]!));
  return escaped.replace(
    HIGHLIGHT_PATTERN,
    (match, comment, str, variable, directive, spread, keyword) => {
      if (comment) return `<span class="gql-comment">${comment}</span>`;
      if (str) return `<span class="gql-string">${str}</span>`;
      if (variable) return `<span class="gql-variable">${variable}</span>`;
      if (directive) return `<span class="gql-directive">${directive}</span>`;
      if (spread) return `<span class="gql-spread">${spread}</span>`;
      if (keyword) return `<span class="gql-keyword">${keyword}</span>`;
      return match;
    },
  );
}
