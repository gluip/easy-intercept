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
  const blocks: Record<number, { type: string; data: Record<string, unknown> }> = {};

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
      const delta = data.delta as Record<string, unknown> | undefined;
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
