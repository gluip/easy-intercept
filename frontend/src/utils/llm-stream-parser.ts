/** Detect if response body is a streaming SSE response */
export function isStreamingResponse(body: string): boolean {
  return body.trim().startsWith("data:") || body.includes("\nevent:");
}

/** Parse OpenAI streaming response (data: {...}\n\ndata: [DONE]) → merged object */
export function parseOpenAIStream(body: string): Record<string, unknown> {
  const lines = body.split("\n").filter((l) => l.trim().startsWith("data:"));
  const events = lines
    .map((l) => l.slice(5).trim())
    .filter((s) => s && s !== "[DONE]")
    .map((s) => {
      try {
        return JSON.parse(s);
      } catch {
        return null;
      }
    })
    .filter((x) => x !== null);

  // Merge all deltas
  let content = "";
  const toolCalls: Record<string, unknown>[] = [];
  let usage: Record<string, unknown> | undefined;
  let finishReason = "";
  let model = "";
  let id = "";

  for (const evt of events as Record<string, unknown>[]) {
    if (evt.model) model = evt.model as string;
    if (evt.id) id = evt.id as string;
    if (evt.usage) usage = evt.usage as Record<string, unknown>;
    const choice = (evt.choices as Record<string, unknown>[])?.[0];
    if (!choice) continue;
    if (choice.finish_reason) finishReason = choice.finish_reason as string;
    const delta = choice.delta as Record<string, unknown> | undefined;
    if (!delta) continue;
    if (delta.content) content += delta.content as string;
    if (Array.isArray(delta.tool_calls)) {
      for (const tc of delta.tool_calls as Record<string, unknown>[]) {
        const idx = tc.index as number;
        if (!toolCalls[idx]) toolCalls[idx] = { id: tc.id, function: { name: "", arguments: "" } };
        if (tc.id) (toolCalls[idx] as Record<string, unknown>).id = tc.id;
        const fn = tc.function as Record<string, unknown> | undefined;
        if (fn) {
          const existing = (toolCalls[idx] as Record<string, unknown>).function as Record<string, unknown>;
          if (fn.name) existing.name = (existing.name ?? "") + (fn.name as string);
          if (fn.arguments) existing.arguments = (existing.arguments ?? "") + (fn.arguments as string);
        }
      }
    }
  }

  return {
    id,
    model,
    usage,
    choices: [
      {
        finish_reason: finishReason,
        message: {
          role: "assistant",
          content: content || undefined,
          tool_calls: toolCalls.length > 0 ? toolCalls : undefined,
        },
      },
    ],
  };
}

/** Parse Anthropic streaming response (event: ...\ndata: {...}) → merged object */
export function parseAnthropicStream(body: string): Record<string, unknown> {
  const lines = body.split("\n");
  const events: { event: string; data: Record<string, unknown> }[] = [];
  let currentEvent = "";

  for (const line of lines) {
    if (line.startsWith("event:")) {
      currentEvent = line.slice(6).trim();
    } else if (line.startsWith("data:")) {
      try {
        const data = JSON.parse(line.slice(5).trim());
        events.push({ event: currentEvent, data });
      } catch {
        /* ignore */
      }
    }
  }

  const content: Record<string, unknown>[] = [];
  let usage: Record<string, unknown> | undefined;
  let stopReason = "";
  let model = "";
  let id = "";

  // Track blocks by index
  const blocks: Record<number, { type: string; data: any }> = {};

  for (const { event, data } of events) {
    if (event === "message_start") {
      const msg = data.message as Record<string, unknown> | undefined;
      if (msg) {
        if (msg.id) id = msg.id as string;
        if (msg.model) model = msg.model as string;
        if (msg.usage) usage = msg.usage as Record<string, unknown>;
      }
    }
    if (event === "content_block_start") {
      const block = data.content_block as Record<string, unknown> | undefined;
      const idx = data.index as number;
      if (block) {
        const type = block.type as string;
        blocks[idx] = { type, data: { ...block } };
      }
    }
    if (event === "content_block_delta") {
      const idx = data.index as number;
      const delta = data.delta as any;
      if (!delta || !blocks[idx]) continue;
      const block = blocks[idx];
      if (delta.type === "text_delta") {
        block.data.text = (block.data.text ?? "") + (delta.text ?? "");
      }
      if (delta.type === "thinking_delta") {
        block.data.thinking = (block.data.thinking ?? "") + (delta.thinking ?? "");
      }
      if (delta.type === "input_json_delta") {
        block.data.input = (block.data.input ?? "") + (delta.partial_json ?? "");
      }
    }
    if (event === "content_block_stop") {
      const idx = data.index as number;
      if (blocks[idx]) {
        const b = blocks[idx];
        if (b.type === "text") content.push({ type: "text", text: b.data.text ?? "" });
        if (b.type === "thinking") content.push({ type: "thinking", thinking: b.data.thinking ?? "", signature: b.data.signature ?? "" });
        if (b.type === "tool_use") {
          let input: unknown = {};
          if (b.data.input) {
            try {
              input = JSON.parse(b.data.input as string);
            } catch {
              input = b.data.input;
            }
          }
          content.push({ type: "tool_use", id: b.data.id ?? "", name: b.data.name ?? "", input });
        }
      }
    }
    if (event === "message_delta") {
      const delta = data.delta as Record<string, unknown> | undefined;
      if (delta?.stop_reason) stopReason = delta.stop_reason as string;
      if (data.usage) usage = { ...usage, ...data.usage };
    }
  }

  return { id, model, content, usage, stop_reason: stopReason };
}

/** True if the request body is for the OpenAI Responses API (input[] instead of messages[]) */
export function isOpenAIResponsesRequest(requestBody: string): boolean {
  try {
    const req = JSON.parse(requestBody);
    return Array.isArray(req.input);
  } catch {
    return false;
  }
}

export interface OpenAIResponsesResult {
  model: string;
  status: string;
  output: Record<string, unknown>[];
  promptTokens: number;
  responseTokens: number;
  cachedTokens: number;
  thoughtTokens: number;
  reasoningEffort: string;
}

/** Parse an OpenAI /v1/responses response body — plain JSON or SSE stream — into a normalized result. */
export function parseOpenAIResponses(responseBody: string): OpenAIResponsesResult {
  const result: OpenAIResponsesResult = {
    model: "unknown", status: "", output: [],
    promptTokens: 0, responseTokens: 0, cachedTokens: 0, thoughtTokens: 0,
    reasoningEffort: "",
  };

  let resp: Record<string, unknown> | undefined;

  if (isStreamingResponse(responseBody)) {
    const lines = responseBody.split("\n");
    for (let i = 0; i < lines.length; i++) {
      if (lines[i].trim() === "event: response.completed" && i + 1 < lines.length) {
        const dataLine = lines[i + 1];
        if (!dataLine.startsWith("data:")) continue;
        try {
          const obj = JSON.parse(dataLine.slice(5).trim());
          resp = obj.response as Record<string, unknown>;
        } catch { /* ignore */ }
      }
    }
  } else {
    try {
      resp = JSON.parse(responseBody);
    } catch { /* ignore */ }
  }

  if (!resp) return result;

  result.model = (resp.model as string) ?? "unknown";
  result.status = (resp.status as string) ?? "";
  result.output = (resp.output as Record<string, unknown>[]) ?? [];

  const u = (resp.usage as Record<string, unknown>) ?? {};
  const inputDetails = (u.input_tokens_details as Record<string, unknown>) ?? {};
  const outputDetails = (u.output_tokens_details as Record<string, unknown>) ?? {};
  result.promptTokens = (u.input_tokens as number) ?? 0;
  result.responseTokens = (u.output_tokens as number) ?? 0;
  result.cachedTokens = (inputDetails.cached_tokens as number) ?? 0;
  result.thoughtTokens = (outputDetails.reasoning_tokens as number) ?? 0;
  result.reasoningEffort = ((resp.reasoning as Record<string, unknown>)?.effort as string) ?? "";

  return result;
}

export interface CopilotResponsesParsed {
  model: string;
  output: { type: string; content?: { type: string; text: string }[]; name?: string; arguments?: string; call_id?: string }[];
  promptTokens: number;
  responseTokens: number;
  cachedTokens: number;
}

/** Parse GitHub Copilot /responses SSE stream → structured result */
export function parseCopilotResponsesStream(body: string): CopilotResponsesParsed {
  const lines = body.split("\n");
  let result: CopilotResponsesParsed = { model: "unknown", output: [], promptTokens: 0, responseTokens: 0, cachedTokens: 0 };

  for (let i = 0; i < lines.length; i++) {
    if (lines[i].trim() === "event: response.completed" && i + 1 < lines.length) {
      const dataLine = lines[i + 1];
      if (!dataLine.startsWith("data:")) continue;
      try {
        const obj = JSON.parse(dataLine.slice(5).trim());
        const resp = obj.response ?? {};
        result.model = resp.model ?? "unknown";
        result.output = resp.output ?? [];
        const details: { token_type: string; token_count: number }[] = obj.copilot_usage?.token_details ?? [];
        for (const td of details) {
          if (td.token_type === "input") result.promptTokens = td.token_count;
          else if (td.token_type === "cache_read") result.cachedTokens = td.token_count;
          else if (td.token_type === "output") result.responseTokens = td.token_count;
        }
      } catch { /* ignore */ }
    }
  }
  return result;
}
