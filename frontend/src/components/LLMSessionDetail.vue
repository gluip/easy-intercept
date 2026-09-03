<script setup lang="ts">
import { computed, ref } from "vue";
import type { ProxySession } from "../types";
import { detectLLMProvider } from "../utils/llm-detection";
import { calcCost, formatCost } from "../utils/llm-cost";
import { isStreamingResponse, parseOpenAIStream, parseAnthropicStream, parseCopilotResponsesStream, isOpenAIResponsesRequest, parseOpenAIResponses } from "../utils/llm-stream-parser";
import { isGeminiInteractionsRequest, parseGeminiInteractionsRequest, parseGeminiInteractionsResponse, geminiInteractionsStepsToParts } from "../utils/gemini-interactions";
import type { GPart, GTurn, ToolDef, ParsedLLM } from "../utils/llm-types";

const props = defineProps<{
  session: ProxySession;
}>();

const emit = defineEmits<{
  openViewer: [session: ProxySession, tab: "request" | "response"];
}>();

// ── Parse ──────────────────────────────────────────────────

const provider = computed(() => detectLLMProvider(props.session));

/** Normalize an Anthropic content block array / string to GPart[] */
function anthropicContent(content: unknown): GPart[] {
  if (typeof content === "string") return [{ text: content }];
  if (!Array.isArray(content)) return [];
  return (content as Record<string, unknown>[]).flatMap((block): GPart[] => {
    if (block.type === "text") return [{ text: block.text as string }];
    if (block.type === "thinking") return [{ thinking: block.thinking as string, thoughtSignature: block.signature as string }];
    if (block.type === "tool_use")
      return [{ functionCall: { id: block.id as string, name: block.name as string, args: (block.input ?? {}) as Record<string, unknown> } }];
    if (block.type === "tool_result") {
      const resp = Array.isArray(block.content)
        ? (block.content as Record<string, unknown>[]).map((c) => c.text).join("\n")
        : block.content;
      return [{ functionResponse: { name: block.tool_use_id as string, response: resp } }];
    }
    return [];
  });
}

/** Normalize OpenAI messages[] into turns + system prompt.
 *  - tool_calls[].function.arguments is a JSON string → parse it
 *  - role=tool messages become functionResponse parts (name resolved from call_id map)
 */
function parseOpenAIMessages(
  messages: Record<string, unknown>[],
): { turns: GTurn[]; system?: string } {
  // Build call_id → function name map
  const callIdToName = new Map<string, string>();
  for (const msg of messages) {
    if (Array.isArray(msg.tool_calls)) {
      for (const tc of msg.tool_calls as Record<string, unknown>[]) {
        const fn = tc.function as Record<string, unknown>;
        callIdToName.set(tc.id as string, fn.name as string);
      }
    }
  }

  let system: string | undefined;
  const turns: GTurn[] = [];

  for (const msg of messages) {
    const role = msg.role as string;

    if (role === "system") {
      const c = msg.content;
      if (typeof c === "string") system = (system ? system + "\n" : "") + c;
      continue;
    }

    if (role === "user") {
      const parts: GPart[] = [];
      if (typeof msg.content === "string" && msg.content)
        parts.push({ text: msg.content });
      else if (Array.isArray(msg.content)) {
        for (const b of msg.content as Record<string, unknown>[]) {
          if (b.type === "text" && b.text) parts.push({ text: b.text as string });
        }
      }
      if (parts.length) turns.push({ role: "user", parts });
      continue;
    }

    if (role === "assistant") {
      const parts: GPart[] = [];
      if (typeof msg.content === "string" && msg.content)
        parts.push({ text: msg.content });
      if (Array.isArray(msg.tool_calls)) {
        for (const tc of msg.tool_calls as Record<string, unknown>[]) {
          const fn = tc.function as Record<string, unknown>;
          let args: Record<string, unknown> = {};
          try { args = JSON.parse(fn.arguments as string); } catch { /* ignore */ }
          parts.push({ functionCall: { id: tc.id as string, name: fn.name as string, args } });
        }
      }
      if (parts.length) turns.push({ role: "model", parts });
      continue;
    }

    if (role === "tool") {
      const callId = msg.tool_call_id as string;
      const name = callIdToName.get(callId) ?? callId;
      turns.push({ role: "user", parts: [{ functionResponse: { name, response: msg.content } }] });
    }
  }

  return { turns, system };
}

/** Normalize OpenAI Responses API input[] into turns + developer/system prompt.
 *  Flat items: {type:"message", role, content}, {type:"function_call", ...}, {type:"function_call_output", ...}
 */
function parseOpenAIResponsesInput(
  input: Record<string, unknown>[],
): { turns: GTurn[]; system?: string } {
  const callIdToName = new Map<string, string>();
  for (const item of input) {
    if (item.type === "function_call") callIdToName.set(item.call_id as string, item.name as string);
  }

  let system: string | undefined;
  const turns: GTurn[] = [];

  for (const item of input) {
    const type = (item.type as string | undefined) ?? "message";

    if (type === "message") {
      const role = item.role as string;
      const content = item.content;
      const parts: GPart[] = [];
      if (typeof content === "string" && content) parts.push({ text: content });
      else if (Array.isArray(content)) {
        for (const b of content as Record<string, unknown>[]) {
          if ((b.type === "input_text" || b.type === "output_text") && b.text) parts.push({ text: b.text as string });
        }
      }
      if (role === "developer" || role === "system") {
        const text = parts.map((p) => p.text ?? "").join("\n");
        if (text) system = system ? system + "\n" + text : text;
        continue;
      }
      if (parts.length) turns.push({ role: role === "assistant" ? "model" : "user", parts });
      continue;
    }

    if (type === "function_call") {
      let args: Record<string, unknown> = {};
      try { args = JSON.parse((item.arguments as string) ?? "{}"); } catch { /* ignore */ }
      turns.push({ role: "model", parts: [{ functionCall: { id: item.call_id as string, name: item.name as string, args } }] });
      continue;
    }

    if (type === "function_call_output") {
      const callId = item.call_id as string;
      const name = callIdToName.get(callId) ?? callId;
      turns.push({ role: "user", parts: [{ functionResponse: { name, response: item.output } }] });
    }
  }

  return { turns, system };
}

/** Normalize OpenAI Responses API output[] into GPart[] (message text, function_call, reasoning summary) */
function openAIResponsesOutputToParts(output: Record<string, unknown>[]): GPart[] {
  const parts: GPart[] = [];
  for (const item of output) {
    const type = item.type as string;
    if (type === "message") {
      for (const c of (item.content ?? []) as Record<string, unknown>[]) {
        if (c.type === "output_text" && c.text) parts.push({ text: c.text as string });
      }
    } else if (type === "function_call") {
      let args: Record<string, unknown> = {};
      try { args = JSON.parse((item.arguments as string) ?? "{}"); } catch { /* ignore */ }
      parts.push({ functionCall: { id: item.call_id as string, name: item.name as string, args } });
    } else if (type === "reasoning") {
      const summary = ((item.summary ?? []) as Record<string, unknown>[]).map((s) => (s.text as string) ?? "").join("\n");
      if (summary) parts.push({ thinking: summary });
    }
  }
  return parts;
}

const parsed = computed((): ParsedLLM | null => {
  try {
    const req = JSON.parse(props.session.requestBody);

    // Copilot /responses is SSE — handled entirely by parseCopilotResponsesStream
    if (provider.value === "copilot") {
      const input: { role: string; content: { type: string; text?: string }[] | string }[] = req.input ?? [];
      const turns: GTurn[] = [];
      let system: string | undefined;

      for (const item of input) {
        const parts: GPart[] = Array.isArray(item.content)
          ? item.content.filter((c) => c.type === "input_text" && c.text).map((c) => ({ text: c.text as string }))
          : [{ text: item.content as string }];

        if (item.role === "system") {
          system = parts.map((p) => p.text ?? "").join("\n");
        } else {
          turns.push({ role: item.role === "assistant" ? "model" : "user", parts });
        }
      }

      const cp = parseCopilotResponsesStream(props.session.responseBody);
      const responseParts: GPart[] = [];
      for (const item of cp.output) {
        if (item.type === "message") {
          for (const c of item.content ?? []) {
            if (c.type === "output_text") responseParts.push({ text: c.text });
          }
        } else if (item.type === "function_call") {
          let args: Record<string, unknown> = {};
          try { args = JSON.parse(item.arguments ?? "{}"); } catch { /* ignore */ }
          responseParts.push({ functionCall: { id: item.call_id ?? "", name: item.name ?? "", args } });
        }
      }

      const copilotTools: ToolDef[] = ((req.tools ?? []) as Record<string, unknown>[]).map((t) => ({
        name: t.name as string,
        description: t.description as string | undefined,
        parameters: t.parameters,
      }));

      return {
        provider: "copilot",
        modelVersion: cp.model,
        system,
        turns,
        responseTurn: responseParts.length ? { role: "model", parts: responseParts } : null,
        tools: copilotTools,
        promptTokens: cp.promptTokens,
        responseTokens: cp.responseTokens,
        cachedTokens: cp.cachedTokens,
        thoughtTokens: 0,
        finishReason: "",
      };
    }

    if (provider.value === "gemini" && isGeminiInteractionsRequest(props.session.requestBody)) {
      // null when the body isn't usable — fall through to the raw-body fallback
      const r = parseGeminiInteractionsResponse(props.session.responseBody);
      if (!r) return null;
      const { turns, system, tools } = parseGeminiInteractionsRequest(req);
      const responseParts = geminiInteractionsStepsToParts(r.steps);
      return {
        provider: "gemini",
        modelVersion: r.model !== "unknown" ? r.model : (req.model ?? "unknown"),
        system,
        turns,
        responseTurn: responseParts.length ? { role: "model", parts: responseParts } : null,
        tools,
        promptTokens: r.promptTokens,
        responseTokens: r.responseTokens,
        cachedTokens: r.cachedTokens,
        thoughtTokens: r.thoughtTokens,
        finishReason: r.status,
      };
    }

    let res: any;

    // If response is streaming SSE, parse it first
    if (isStreamingResponse(props.session.responseBody)) {
      if (provider.value === "openai") {
        res = parseOpenAIStream(props.session.responseBody);
      } else if (provider.value === "anthropic") {
        res = parseAnthropicStream(props.session.responseBody);
      } else {
        res = JSON.parse(props.session.responseBody);
      }
    } else {
      res = JSON.parse(props.session.responseBody);
    }

    if (provider.value === "gemini") {
      const u = res.usageMetadata ?? {};
      const cand = res.candidates?.[0];
      // Extract Gemini tool declarations
      const geminiTools: ToolDef[] = (req.tools ?? []).flatMap(
        (t: Record<string, unknown>) =>
          ((t.functionDeclarations ?? []) as Record<string, unknown>[]).map((fn) => ({
            name: fn.name as string,
            description: fn.description as string | undefined,
            parameters: fn.parameters,
          })),
      );
      return {
        provider: "gemini",
        modelVersion: res.modelVersion ?? "unknown",
        turns: Array.isArray(req.contents) ? req.contents : [],
        responseTurn: cand?.content ?? null,
        tools: geminiTools,
        promptTokens: u.promptTokenCount ?? 0,
        responseTokens: u.candidatesTokenCount ?? 0,
        cachedTokens: u.cachedContentTokenCount ?? 0,
        thoughtTokens: u.thoughtsTokenCount ?? 0,
        finishReason: cand?.finishReason ?? "",
      };
    }

    if (provider.value === "anthropic") {
      const u = res.usage ?? {};
      // Normalize system prompt to plain string
      const sys = req.system;
      let system: string | undefined;
      if (typeof sys === "string") system = sys;
      else if (Array.isArray(sys))
        system = (sys as Record<string, unknown>[]).map((b) => b.text ?? "").join("\n");
      // Normalize messages
      const turns: GTurn[] = (req.messages ?? []).map((msg: Record<string, unknown>) => ({
        role: msg.role === "assistant" ? "model" : "user",
        parts: anthropicContent(msg.content),
      }));
      const responseParts = anthropicContent(res.content);
      const responseTurn: GTurn | null = responseParts.length ? { role: "model", parts: responseParts } : null;
      // Extract Anthropic tool declarations
      const anthropicTools: ToolDef[] = ((req.tools ?? []) as Record<string, unknown>[]).map((t) => ({
        name: t.name as string,
        description: t.description as string | undefined,
        parameters: t.input_schema,
      }));
      return {
        provider: "anthropic",
        modelVersion: res.model ?? req.model ?? "unknown",
        system,
        turns,
        responseTurn,
        tools: anthropicTools,
        promptTokens: u.input_tokens ?? 0,
        responseTokens: u.output_tokens ?? 0,
        cachedTokens: u.cache_read_input_tokens ?? 0,
        thoughtTokens: 0,
        finishReason: res.stop_reason ?? "",
      };
    }

    if (provider.value === "openai" && isOpenAIResponsesRequest(props.session.requestBody)) {
      const r = parseOpenAIResponses(props.session.responseBody);
      const { turns, system } = parseOpenAIResponsesInput(req.input ?? []);
      const responseParts = openAIResponsesOutputToParts(r.output);
      const responseTurn: GTurn | null = responseParts.length ? { role: "model", parts: responseParts } : null;
      // Responses API tools are flat {type:"function", name, description, parameters}
      const responsesTools: ToolDef[] = ((req.tools ?? []) as Record<string, unknown>[]).map((t) => ({
        name: t.name as string,
        description: t.description as string | undefined,
        parameters: t.parameters,
      }));
      return {
        provider: "openai",
        modelVersion: r.model !== "unknown" ? r.model : (req.model ?? "unknown"),
        system,
        turns,
        responseTurn,
        tools: responsesTools,
        promptTokens: r.promptTokens,
        responseTokens: r.responseTokens,
        cachedTokens: r.cachedTokens,
        thoughtTokens: r.thoughtTokens,
        finishReason: r.status,
        reasoningEffort: r.reasoningEffort || undefined,
      };
    }

    if (provider.value === "openai") {
      const u = res.usage ?? {};
      const { turns, system } = parseOpenAIMessages(req.messages ?? []);
      // Build response turn from choices[0].message
      const msg = res.choices?.[0]?.message as Record<string, unknown> | undefined;
      const responseParts: GPart[] = [];
      if (msg) {
        if (typeof msg.content === "string" && msg.content)
          responseParts.push({ text: msg.content });
        if (Array.isArray(msg.tool_calls)) {
          for (const tc of msg.tool_calls as Record<string, unknown>[]) {
            const fn = tc.function as Record<string, unknown>;
            let args: Record<string, unknown> = {};
            try { args = JSON.parse(fn.arguments as string); } catch { /* ignore */ }
            responseParts.push({ functionCall: { id: tc.id as string, name: fn.name as string, args } });
          }
        }
      }
      const responseTurn: GTurn | null = responseParts.length
        ? { role: "model", parts: responseParts }
        : null;
      // Extract tool declarations
      const openaiTools: ToolDef[] = ((req.tools ?? []) as Record<string, unknown>[]).map((t) => {
        const fn = t.function as Record<string, unknown>;
        return {
          name: fn.name as string,
          description: fn.description as string | undefined,
          parameters: fn.parameters,
        };
      });
      const promptTokensDetails = u.prompt_tokens_details ?? {};
      return {
        provider: "openai",
        modelVersion: res.model ?? req.model ?? "unknown",
        system,
        turns,
        responseTurn,
        tools: openaiTools,
        promptTokens: u.prompt_tokens ?? 0,
        responseTokens: u.completion_tokens ?? 0,
        cachedTokens: promptTokensDetails.cached_tokens ?? 0,
        thoughtTokens: 0,
        finishReason: res.choices?.[0]?.finish_reason ?? "",
      };
    }

    return null;
  } catch {
    return null;
  }
});
// ── Cost ──────────────────────────────────────────────────

const cost = computed(() => {
  if (!parsed.value || !provider.value) return null;
  return calcCost(
    provider.value,
    parsed.value.modelVersion,
    parsed.value.promptTokens,
    parsed.value.responseTokens,
    parsed.value.cachedTokens,
    parsed.value.thoughtTokens,
  );
});
// ── Expand / collapse ──────────────────────────────────────

const expandedKeys = ref(new Set<string>());

function toggleKey(key: string) {
  const next = new Set(expandedKeys.value);
  next.has(key) ? next.delete(key) : next.add(key);
  expandedKeys.value = next;
}

function isOpen(key: string) {
  return expandedKeys.value.has(key);
}

// ── Helpers ────────────────────────────────────────────────

function fmtJson(val: unknown): string {
  return JSON.stringify(val, null, 2);
}

// Returns true if the turn has any displayable parts
function hasContent(parts: GPart[]): boolean {
  return parts.some((p) => p.text || p.thinking || p.functionCall || p.functionResponse);
}

// Label for the turn's role indicator
function turnLabel(turn: GTurn): "user" | "assistant" | "tool results" {
  if (turn.role === "model") return "assistant";
  const visible = turn.parts.filter(
    (p) => p.text || p.thinking || p.functionCall || p.functionResponse,
  );
  if (visible.length > 0 && visible.every((p) => !!p.functionResponse)) {
    return "tool results";
  }
  return "user";
}

// Arg key preview for collapsed function calls
function argPreview(args: Record<string, unknown> | undefined): string {
  if (!args) return "";
  const keys = Object.keys(args);
  return keys.length ? `(${keys.slice(0, 3).join(", ")}${keys.length > 3 ? ", …" : ""})` : "()";
}
</script>

<template>
  <div class="llm-detail">
    <!-- ── Parse failure ─────────────────────────────────── -->
    <template v-if="!parsed">
      <div class="parse-error">
        <span>Could not parse as LLM response.</span>
        <button class="raw-btn" @click="emit('openViewer', session, 'response')">
          View Raw
        </button>
      </div>
    </template>

    <template v-else>
      <!-- ── Stats bar ─────────────────────────────────────── -->
      <div class="stats-bar">
        <span class="model-name">{{ parsed.modelVersion }}</span>
        <div class="token-pills">
          <span
            v-if="parsed.reasoningEffort"
            class="pill pill-effort"
            title="Reasoning effort"
          >
            🧠 {{ parsed.reasoningEffort }}
          </span>
          <span class="pill pill-prompt" title="Prompt tokens">
            ↑ {{ parsed.promptTokens.toLocaleString() }}
          </span>
          <span
            v-if="parsed.cachedTokens"
            class="pill pill-cached"
            title="Cached tokens"
          >
            💾 {{ parsed.cachedTokens.toLocaleString() }}
          </span>
          <span
            v-if="parsed.thoughtTokens"
            class="pill pill-thoughts"
            title="Thinking tokens"
          >
            💭 {{ parsed.thoughtTokens.toLocaleString() }}
          </span>
          <span class="pill pill-response" title="Response tokens">
            ↓ {{ parsed.responseTokens.toLocaleString() }}
          </span>
          <span class="pill pill-duration" title="Request duration">{{ session.durationMs }}ms</span>
          <span
            v-if="cost"
            class="pill pill-cost"
            :title="cost ? `input $${cost.inputCost.toFixed(4)} + output $${cost.outputCost.toFixed(4)} + cached $${cost.cachedCost.toFixed(4)}` : ''"
          >{{ cost ? formatCost(cost) : '' }}</span>
        </div>
        <div class="stats-actions">
          <button class="raw-btn" @click="emit('openViewer', session, 'request')">
            ⬡ Req
          </button>
          <button class="raw-btn" @click="emit('openViewer', session, 'response')">
            ⬡ Res
          </button>
        </div>
      </div>

      <!-- ── Tools ──────────────────────────────────────────── -->
      <div v-if="parsed.tools.length" class="tools-section">
        <div class="tools-header" @click="toggleKey('__tools__')">
          <span class="tools-icon">⚙</span>
          <span class="tools-title">{{ parsed.tools.length }} tool{{ parsed.tools.length === 1 ? '' : 's' }}</span>
          <span class="tools-toggle">{{ isOpen('__tools__') ? '▼' : '▶' }}</span>
        </div>
        <div v-if="isOpen('__tools__')" class="tools-list">
          <div v-for="tool in parsed.tools" :key="tool.name" class="tool-item">
            <div class="tool-row" @click="toggleKey('tool-' + tool.name)">
              <span class="tool-name">{{ tool.name }}</span>
              <span v-if="tool.description" class="tool-desc">{{ tool.description }}</span>
              <span v-if="tool.parameters" class="tool-schema-toggle">{{ isOpen('tool-' + tool.name) ? '▼' : '▶' }}</span>
            </div>
            <pre v-if="tool.parameters && isOpen('tool-' + tool.name)" class="tool-schema">{{ fmtJson(tool.parameters) }}</pre>
          </div>
        </div>
      </div>

      <!-- ── Conversation ──────────────────────────────────── -->
      <div class="conversation">
        <!-- ── New response (top) ────────────────────────────── -->
        <div v-if="parsed.responseTurn" class="turn turn-model turn-new">
          <div class="turn-label label-assistant">
            assistant
            <span class="new-badge">new</span>
          </div>
          <div class="turn-body">
            <template
              v-for="(part, pIdx) in parsed.responseTurn.parts"
              :key="pIdx"
            >
              <div v-if="part.thinking" class="part-thinking">
                <div class="thinking-label">💭 thinking</div>
                <div class="thinking-text">{{ part.thinking }}</div>
              </div>
              <div v-else-if="part.text" class="part-text">{{ part.text }}</div>
              <div v-else-if="part.functionCall" class="part-fn-call">
                <div class="fn-header" @click="toggleKey(`new-${pIdx}`)">
                  <span class="fn-icon">⚙</span>
                  <span class="fn-name">{{ part.functionCall.name }}</span>
                  <span
                    v-if="!isOpen(`new-${pIdx}`)"
                    class="fn-args-preview"
                  >
                    {{ argPreview(part.functionCall.args) }}
                  </span>
                  <span class="fn-toggle">
                    {{ isOpen(`new-${pIdx}`) ? "▼" : "▶" }}
                  </span>
                </div>
                <pre
                  v-if="isOpen(`new-${pIdx}`)"
                  class="fn-body"
                >{{ fmtJson(part.functionCall.args) }}</pre>
              </div>
            </template>
          </div>
        </div>

        <!-- History turns (reversed: newest first) -->
        <template v-for="(turn, tIdx) in [...parsed.turns].reverse()" :key="tIdx">
          <div
            v-if="hasContent(turn.parts)"
            :class="[
              'turn',
              `turn-${turn.role}`,
              { 'turn-tool-results': turnLabel(turn) === 'tool results' },
            ]"
          >
            <div :class="['turn-label', `label-${turnLabel(turn).replace(' ', '-')}`]">
              {{ turnLabel(turn) }}
            </div>
            <div class="turn-body">
              <template v-for="(part, pIdx) in turn.parts" :key="pIdx">
                <!-- Thinking -->
                <div v-if="part.thinking" class="part-thinking">
                  <div class="thinking-label">💭 thinking</div>
                  <div class="thinking-text">{{ part.thinking }}</div>
                </div>

                <!-- Text -->
                <div v-else-if="part.text" class="part-text">{{ part.text }}</div>

                <!-- Function call -->
                <div v-else-if="part.functionCall" class="part-fn-call">
                  <div
                    class="fn-header"
                    @click="toggleKey(`${tIdx}-${pIdx}`)"
                  >
                    <span class="fn-icon">⚙</span>
                    <span class="fn-name">{{ part.functionCall.name }}</span>
                    <span
                      v-if="!isOpen(`${tIdx}-${pIdx}`)"
                      class="fn-args-preview"
                    >
                      {{ argPreview(part.functionCall.args) }}
                    </span>
                    <span class="fn-toggle">
                      {{ isOpen(`${tIdx}-${pIdx}`) ? "▼" : "▶" }}
                    </span>
                  </div>
                  <pre
                    v-if="isOpen(`${tIdx}-${pIdx}`)"
                    class="fn-body"
                  >{{ fmtJson(part.functionCall.args) }}</pre>
                </div>

                <!-- Function response -->
                <div v-else-if="part.functionResponse" class="part-fn-response">
                  <div
                    class="fn-header"
                    @click="toggleKey(`r${tIdx}-${pIdx}`)"
                  >
                    <span class="fn-icon">↩</span>
                    <span class="fn-name">{{ part.functionResponse.name }}</span>
                    <span class="fn-toggle">
                      {{ isOpen(`r${tIdx}-${pIdx}`) ? "▼" : "▶" }}
                    </span>
                  </div>
                  <pre
                    v-if="isOpen(`r${tIdx}-${pIdx}`)"
                    class="fn-body"
                  >{{ fmtJson(part.functionResponse.response) }}</pre>
                </div>
              </template>
            </div>
          </div>
        </template>

        <!-- ── System prompt (Anthropic) ─────────────────────── -->
        <div v-if="parsed.system" class="turn turn-system">
          <div class="turn-label label-system">system</div>
          <div class="turn-body">
            <div class="part-text">{{ parsed.system }}</div>
          </div>
        </div>
      </div>
    </template>
  </div>
</template>

<style scoped>
.llm-detail {
  display: flex;
  flex-direction: column;
  gap: 0;
}

/* ── Stats bar ──────────────────────────────────────────── */
.stats-bar {
  display: flex;
  align-items: center;
  gap: 10px;
  padding: 8px 10px;
  background: #252526;
  border: 1px solid #3e3e42;
  border-radius: 3px;
  margin-bottom: 10px;
  flex-wrap: wrap;
}

.model-name {
  font-size: 11px;
  font-weight: 600;
  color: #4ec9b0;
  font-family: "Courier New", monospace;
  flex-shrink: 0;
}

.token-pills {
  display: flex;
  gap: 5px;
  flex-wrap: wrap;
  flex: 1;
}

.pill {
  font-size: 10px;
  padding: 2px 7px;
  border-radius: 10px;
  font-family: "Courier New", monospace;
}
.pill-prompt {
  background: #1e2a3f;
  color: #569cd6;
}
.pill-cached {
  background: #1e3a1e;
  color: #4ec9b0;
}
.pill-thoughts {
  background: #2a1e3a;
  color: #c586c0;
}
.pill-effort {
  background: #2a1e3a;
  color: #c586c0;
  text-transform: capitalize;
}
.pill-response {
  background: #3a2e1e;
  color: #dcdcaa;
}
.pill-duration {
  background: #2a2a2a;
  color: #858585;
}
.pill-cost {
  background: #1e2a1e;
  color: #4ec9b0;
  font-weight: 600;
  cursor: default;
}

.stats-actions {
  display: flex;
  gap: 4px;
  flex-shrink: 0;
}

.raw-btn {
  background: transparent;
  color: #569cd6;
  border: 1px solid #3e3e42;
  font-size: 10px;
  padding: 2px 7px;
  border-radius: 3px;
  cursor: pointer;
  font-family: inherit;
}
.raw-btn:hover {
  border-color: #569cd6;
}

/* ── Tools section ────────────────────────────────────────── */
.tools-section {
  border: 1px solid #3e3e42;
  border-radius: 3px;
  margin-bottom: 10px;
  overflow: hidden;
  background: #1e1e1e;
}

.tools-header {
  display: flex;
  align-items: center;
  gap: 6px;
  padding: 6px 10px;
  background: #252526;
  cursor: pointer;
  user-select: none;
  font-size: 11px;
}
.tools-header:hover {
  background: #2a2d2e;
}

.tools-icon {
  color: #dcdcaa;
  font-size: 11px;
}
.tools-title {
  color: #dcdcaa;
  font-weight: 600;
  flex: 1;
}
.tools-toggle {
  color: #858585;
  font-size: 9px;
}

.tools-list {
  display: flex;
  flex-direction: column;
  gap: 0;
}

.tool-item {
  border-top: 1px solid #3e3e42;
}

.tool-row {
  display: flex;
  align-items: baseline;
  gap: 8px;
  padding: 5px 10px;
  cursor: pointer;
  user-select: none;
}
.tool-row:hover {
  background: #2a2d2e;
}

.tool-name {
  font-size: 11px;
  font-family: "Courier New", monospace;
  color: #569cd6;
  flex-shrink: 0;
}
.tool-desc {
  font-size: 11px;
  color: #9d9d9d;
  flex: 1;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}
.tool-schema-toggle {
  color: #858585;
  font-size: 9px;
  flex-shrink: 0;
}

.tool-schema {
  margin: 0;
  padding: 8px 12px;
  background: #0d0d0d;
  font-size: 11px;
  font-family: "Courier New", monospace;
  color: #ce9178;
  overflow-x: auto;
  border-top: 1px solid #3e3e42;
  white-space: pre;
}

/* ── Conversation ─────────────────────────────────────────── */
.conversation {
  display: flex;
  flex-direction: column;
  gap: 6px;
}

/* ── Turn ─────────────────────────────────────────────────── */
.turn {
  border-radius: 3px;
  border: 1px solid #3e3e42;
  overflow: hidden;
}

.turn-tool-results {
  border-color: #2a3a2a;
  opacity: 0.9;
}

.turn-new {
  border-color: #5a4a1e;
}

.turn-label {
  font-size: 10px;
  font-weight: 600;
  text-transform: uppercase;
  letter-spacing: 0.06em;
  padding: 4px 8px;
  display: flex;
  align-items: center;
  gap: 6px;
}

.label-user {
  background: #242424;
  color: #858585;
}
.label-assistant {
  background: #1a2535;
  color: #569cd6;
}
.label-tool-results {
  background: #1a2a1a;
  color: #4ec9b0;
}
.label-system {
  background: #1e1a2a;
  color: #c586c0;
}

.turn-system {
  border-left: 2px solid #3a2a4a;
  opacity: 0.75;
}

.turn-new .turn-label {
  background: #2a2010;
  color: #dcdcaa;
}

.new-badge {
  background: #5a4a1e;
  color: #dcdcaa;
  font-size: 9px;
  padding: 1px 5px;
  border-radius: 8px;
  text-transform: lowercase;
  font-weight: normal;
  letter-spacing: 0;
}

.turn-body {
  padding: 8px;
  display: flex;
  flex-direction: column;
  gap: 4px;
}

/* ── Parts ────────────────────────────────────────────────── */
.part-text {
  font-size: 12px;
  color: #d4d4d4;
  line-height: 1.55;
  white-space: pre-wrap;
  word-break: break-word;
}

/* Thinking */
.part-thinking {
  border: 1px solid #3e3e42;
  border-radius: 3px;
  background: #1a1a1a;
  overflow: hidden;
}

.thinking-label {
  font-size: 10px;
  font-weight: 600;
  text-transform: uppercase;
  letter-spacing: 0.06em;
  padding: 4px 8px;
  background: #252526;
  color: #c586c0;
  border-bottom: 1px solid #3e3e42;
}

.thinking-text {
  font-size: 11px;
  color: #9d9d9d;
  line-height: 1.5;
  white-space: pre-wrap;
  word-break: break-word;
  padding: 8px;
  font-style: italic;
}

/* Function call */
.part-fn-call {
  border: 1px solid #2a3a4a;
  border-radius: 3px;
  overflow: hidden;
}

.part-fn-response {
  border: 1px solid #1e3a2f;
  border-radius: 3px;
  overflow: hidden;
}

.fn-header {
  display: flex;
  align-items: center;
  gap: 6px;
  padding: 5px 8px;
  cursor: pointer;
  font-size: 11px;
  user-select: none;
}

.part-fn-call .fn-header {
  background: #1a2535;
  color: #9cdcfe;
}
.part-fn-call .fn-header:hover {
  background: #1e2f45;
}

.part-fn-response .fn-header {
  background: #192b22;
  color: #4ec9b0;
}
.part-fn-response .fn-header:hover {
  background: #1e3a2f;
}

.fn-icon {
  font-size: 11px;
  opacity: 0.6;
  flex-shrink: 0;
}

.fn-name {
  font-family: "Courier New", monospace;
  font-weight: 600;
}

.fn-args-preview {
  color: #6a8a9a;
  font-family: "Courier New", monospace;
  font-size: 10px;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  max-width: 180px;
}

.fn-toggle {
  margin-left: auto;
  font-size: 9px;
  opacity: 0.45;
}

.fn-body {
  background: #161616;
  border-top: 1px solid #2a2a2a;
  margin: 0;
  padding: 8px;
  font-size: 11px;
  color: #d4d4d4;
  overflow-x: auto;
  max-height: 280px;
  overflow-y: auto;
  white-space: pre;
}

/* ── Parse error ──────────────────────────────────────────── */
.parse-error {
  padding: 20px;
  color: #858585;
  font-size: 12px;
  display: flex;
  align-items: center;
  gap: 10px;
}
</style>
