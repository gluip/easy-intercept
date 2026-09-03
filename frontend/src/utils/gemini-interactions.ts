// Google Gemini Interactions API (POST /v1beta/interactions).
//
// A stateful, OpenAI-Responses-shaped successor to generateContent. The request carries
// system_instruction + a flat input[] instead of contents[]; the response carries steps[]
// + usage instead of candidates[] + usageMetadata. Bodies are plain JSON (no SSE observed).

import type { GPart, GTurn, ToolDef } from "./llm-types";
import { isStreamingResponse } from "./llm-stream-parser";

/** True if the request body is for the Interactions API (input[] instead of contents[]) */
export function isGeminiInteractionsRequest(requestBody: string): boolean {
  try {
    const req = JSON.parse(requestBody);
    return Array.isArray(req.input) && !Array.isArray(req.contents);
  } catch {
    return false;
  }
}

export interface GeminiInteractionsResult {
  model: string;
  status: string;
  steps: Record<string, unknown>[];
  promptTokens: number;
  responseTokens: number;
  cachedTokens: number;
  thoughtTokens: number;
}

/** Parse an Interactions response body — plain JSON or SSE — into a normalized result.
 *  Returns null when the body isn't usable (empty, in-flight, or an error page), so callers
 *  can show the raw-body fallback instead of an empty transcript. */
export function parseGeminiInteractionsResponse(responseBody: string): GeminiInteractionsResult | null {
  let resp: Record<string, unknown> | undefined;

  if (isStreamingResponse(responseBody)) {
    // Streaming isn't documented for this endpoint yet — keep the last frame carrying steps[]
    for (const line of responseBody.split("\n")) {
      if (!line.startsWith("data:")) continue;
      try {
        const obj = JSON.parse(line.slice(5).trim());
        if (obj && Array.isArray(obj.steps)) resp = obj;
      } catch { /* ignore */ }
    }
  } else {
    try {
      resp = JSON.parse(responseBody);
    } catch { /* ignore */ }
  }

  if (!resp) return null;

  const result: GeminiInteractionsResult = {
    model: "unknown", status: "", steps: [],
    promptTokens: 0, responseTokens: 0, cachedTokens: 0, thoughtTokens: 0,
  };

  result.model = (resp.model as string) ?? "unknown";
  result.status = (resp.status as string) ?? "";
  result.steps = (resp.steps as Record<string, unknown>[]) ?? [];

  // total_tokens = total_input_tokens + total_output_tokens + total_thought_tokens;
  // total_cached_tokens is a subset of total_input_tokens (same convention as generateContent).
  const u = (resp.usage as Record<string, unknown>) ?? {};
  result.promptTokens   = (u.total_input_tokens   as number) ?? 0;
  result.responseTokens = (u.total_output_tokens  as number) ?? 0;
  result.cachedTokens   = (u.total_cached_tokens  as number) ?? 0;
  result.thoughtTokens  = (u.total_thought_tokens as number) ?? 0;

  return result;
}

/** Collect the text out of a content[]/result[] array. Items are {type:"text", text},
 *  but we key off .text alone so a missing/renamed type doesn't drop the content. */
function contentText(content: unknown): string[] {
  if (typeof content === "string") return content ? [content] : [];
  if (!Array.isArray(content)) return [];
  return (content as Record<string, unknown>[])
    .map((c) => c?.text)
    .filter((t): t is string => typeof t === "string" && t.length > 0);
}

/** function_call item/step → GPart. Note `arguments` is already an object here,
 *  unlike OpenAI where it is a JSON string that needs parsing. */
function functionCallPart(item: Record<string, unknown>): GPart {
  const args = item.arguments;
  return {
    functionCall: {
      id: item.id as string | undefined,
      name: (item.name as string) ?? "",
      args: (args !== null && typeof args === "object" ? args : {}) as Record<string, unknown>,
    },
  };
}

/** Normalize the response steps[] into GPart[].
 *  `thought` steps carry only an opaque base64 signature, so they yield nothing renderable. */
export function geminiInteractionsStepsToParts(steps: Record<string, unknown>[]): GPart[] {
  const parts: GPart[] = [];
  for (const step of steps ?? []) {
    const type = step.type as string;
    if (type === "model_output") {
      for (const text of contentText(step.content)) parts.push({ text });
    } else if (type === "function_call") {
      parts.push(functionCallPart(step));
    }
  }
  return parts;
}

/** Normalize an Interactions request into turns + system prompt + tool declarations. */
export function parseGeminiInteractionsRequest(
  req: Record<string, unknown>,
): { turns: GTurn[]; system?: string; tools: ToolDef[] } {
  const input = (req.input ?? []) as Record<string, unknown>[];

  // function_result carries its own `name`, but fall back to the call id map if it ever doesn't
  const callIdToName = new Map<string, string>();
  for (const item of input) {
    if (item.type === "function_call" && item.id) {
      callIdToName.set(item.id as string, item.name as string);
    }
  }

  const turns: GTurn[] = [];

  for (const item of input) {
    const type = item.type as string;

    if (type === "user_input" || type === "model_output") {
      const parts: GPart[] = contentText(item.content).map((text) => ({ text }));
      if (parts.length) turns.push({ role: type === "model_output" ? "model" : "user", parts });
      continue;
    }

    if (type === "function_call") {
      turns.push({ role: "model", parts: [functionCallPart(item)] });
      continue;
    }

    if (type === "function_result") {
      const callId = item.call_id as string;
      const name = (item.name as string) ?? callIdToName.get(callId) ?? callId;
      const texts = contentText(item.result);
      turns.push({
        role: "user",
        parts: [{ functionResponse: { name, response: texts.length ? texts.join("\n") : item.result } }],
      });
    }

    // "thought" items carry only an opaque signature — nothing to show
  }

  // Interactions tools are flat {type:"function", name, description, parameters},
  // not wrapped in functionDeclarations like generateContent
  const tools: ToolDef[] = ((req.tools ?? []) as Record<string, unknown>[]).map((t) => ({
    name: t.name as string,
    description: t.description as string | undefined,
    parameters: t.parameters,
  }));

  const sys = req.system_instruction;

  return { turns, system: typeof sys === "string" && sys ? sys : undefined, tools };
}

/** First model_output text in the response steps — used for the list preview. */
export function geminiInteractionsPreviewText(steps: Record<string, unknown>[]): string | null {
  for (const step of steps ?? []) {
    if (step.type !== "model_output") continue;
    const [text] = contentText(step.content);
    if (text) return text;
  }
  return null;
}

/** Names of the functions the model called in this response. */
export function geminiInteractionsToolCallNames(steps: Record<string, unknown>[]): string[] {
  return (steps ?? [])
    .filter((s) => s.type === "function_call")
    .map((s) => (s.name as string) ?? "");
}

/** Text of the trailing run of function_result items — the tool output this request feeds back. */
export function geminiInteractionsTrailingResults(input: Record<string, unknown>[]): string[] {
  const items = input ?? [];
  const trailing: Record<string, unknown>[] = [];
  for (let i = items.length - 1; i >= 0; i--) {
    if (items[i].type !== "function_result") break;
    trailing.unshift(items[i]);
  }
  return trailing.map((item) => {
    const texts = contentText(item.result);
    return texts.length ? texts.join("\n") : JSON.stringify(item.result ?? "");
  });
}
