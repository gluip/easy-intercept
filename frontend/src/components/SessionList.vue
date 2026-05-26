<script setup lang="ts">
import { ref, computed, onMounted, onUnmounted } from "vue";
import type { ProxySession } from "../types";
import ContextMenu from "./ContextMenu.vue";
import { detectLLMProvider } from "../utils/llm-detection";
import { isStreamingResponse, parseOpenAIStream, parseAnthropicStream } from "../utils/llm-stream-parser";

const props = defineProps<{
  sessions: readonly ProxySession[];
  selectedIds: string[];
}>();

const emit = defineEmits<{
  select: [ids: string[]];
  copyUrl: [session: ProxySession];
  replay: [session: ProxySession];
  deleteSelected: [ids: string[]];
}>();

const listEl = ref<HTMLElement>();
const lastClickedId = ref<string | null>(null);
const filterText = ref("");
const llmOnly = ref(false);

const ctxMenu = ref<{ session: ProxySession; x: number; y: number } | null>(
  null,
);

const selectedSet = computed(() => new Set(props.selectedIds));

const filteredSessions = computed(() => {
  let result = props.sessions;
  
  // Filter by LLM only
  if (llmOnly.value) {
    result = result.filter((s) => detectLLMProvider(s) !== null);
  }
  
  // Filter by URL text
  const needle = filterText.value.toLowerCase().trim();
  if (needle) {
    result = result.filter((s) => s.url.toLowerCase().includes(needle));
  }
  
  return result;
});

const menuItems = [
  { label: "Copy URL", icon: "📋", action: "copy-url" },
  { label: "Replay", icon: "🔁", action: "replay" },
  { label: "Delete", icon: "🗑️", action: "delete" },
];

function handleClick(e: MouseEvent, session: ProxySession) {
  const meta = e.metaKey || e.ctrlKey;
  const shift = e.shiftKey;

  if (shift && lastClickedId.value) {
    // range select
    const ids = props.sessions.map((s) => s.id);
    const from = ids.indexOf(lastClickedId.value);
    const to = ids.indexOf(session.id);
    if (from >= 0 && to >= 0) {
      const [lo, hi] = from < to ? [from, to] : [to, from];
      const range = ids.slice(lo, hi + 1);
      if (meta) {
        const merged = new Set([...props.selectedIds, ...range]);
        emit("select", [...merged]);
      } else {
        emit("select", range);
      }
    }
  } else if (meta) {
    // toggle single
    if (selectedSet.value.has(session.id)) {
      emit(
        "select",
        props.selectedIds.filter((id) => id !== session.id),
      );
    } else {
      emit("select", [...props.selectedIds, session.id]);
    }
  } else {
    emit("select", [session.id]);
  }
  lastClickedId.value = session.id;
}

function onContextMenu(e: MouseEvent, session: ProxySession) {
  e.preventDefault();
  // if right-clicked session not in selection, select it
  if (!selectedSet.value.has(session.id)) {
    emit("select", [session.id]);
    lastClickedId.value = session.id;
  }
  ctxMenu.value = { session, x: e.clientX, y: e.clientY };
}

function onMenuSelect(action: string) {
  if (!ctxMenu.value) return;
  const session = ctxMenu.value.session;
  ctxMenu.value = null;
  if (action === "copy-url") emit("copyUrl", session);
  else if (action === "replay") emit("replay", session);
  else if (action === "delete") emit("deleteSelected", [...props.selectedIds]);
}

function onKeyDown(e: KeyboardEvent) {
  // only handle when our list is focused
  if (
    !listEl.value?.contains(document.activeElement) &&
    document.activeElement !== listEl.value
  )
    return;

  if (
    (e.key === "Delete" || e.key === "Backspace") &&
    props.selectedIds.length > 0
  ) {
    e.preventDefault();
    emit("deleteSelected", [...props.selectedIds]);
  }
  if ((e.metaKey || e.ctrlKey) && e.key === "a") {
    e.preventDefault();
    emit(
      "select",
      props.sessions.map((s) => s.id),
    );
  }
  if (e.key === "ArrowDown" || e.key === "ArrowUp") {
    e.preventDefault();
    const ids = props.sessions.map((s) => s.id);
    if (ids.length === 0) return;
    const lastSelected =
      props.selectedIds.length > 0
        ? props.selectedIds[props.selectedIds.length - 1]
        : null;
    const curIdx = lastSelected ? ids.indexOf(lastSelected) : -1;
    const nextIdx =
      e.key === "ArrowDown"
        ? Math.min(curIdx + 1, ids.length - 1)
        : Math.max(curIdx - 1, 0);
    const nextId = ids[nextIdx];
    if (e.shiftKey) {
      if (!props.selectedIds.includes(nextId)) {
        emit("select", [...props.selectedIds, nextId]);
      } else {
        // shrink selection when going back
        emit(
          "select",
          props.selectedIds.filter((id) => id !== lastSelected),
        );
      }
    } else {
      emit("select", [nextId]);
    }
    lastClickedId.value = nextId;
    // scroll the row into view
    const row = listEl.value?.querySelector(`tr[data-id="${nextId}"]`);
    row?.scrollIntoView({ block: "nearest" });
  }
}

onMounted(() => document.addEventListener("keydown", onKeyDown));
onUnmounted(() => document.removeEventListener("keydown", onKeyDown));

function methodClass(m: string) {
  return ["GET", "POST", "PUT", "DELETE", "PATCH"].includes(m) ? m : "";
}

function statusClass(s: number) {
  if (s >= 500) return "s5";
  if (s >= 400) return "s4";
  if (s >= 300) return "s3";
  return "s2";
}

function isAutoResponse(s: ProxySession) {
  return s.responseHeaders?.["X-EasyIntercept-AutoResponder"] === "true";
}

// ── Column resize ─────────────────────────────────────────

type ColKey = "method" | "status" | "url" | "tools" | "results" | "dur";

const colWidths = ref<Record<ColKey, number>>({
  method: 62,
  status: 46,
  url: 200,
  tools: 130,
  results: 320,
  dur: 56,
});

function startColResize(col: ColKey, e: MouseEvent) {
  e.preventDefault();
  e.stopPropagation();
  const startX = e.clientX;
  const startWidth = colWidths.value[col];
  document.body.style.cursor = "col-resize";
  document.body.style.userSelect = "none";

  function onMove(ev: MouseEvent) {
    colWidths.value[col] = Math.max(40, startWidth + ev.clientX - startX);
  }
  function onUp() {
    document.body.style.cursor = "";
    document.body.style.userSelect = "";
    document.removeEventListener("mousemove", onMove);
    document.removeEventListener("mouseup", onUp);
  }
  document.addEventListener("mousemove", onMove);
  document.addEventListener("mouseup", onUp);
}

function llmPreview(s: ProxySession): string | null {
  const provider = detectLLMProvider(s);
  if (!provider) return null;
  try {
    let res: Record<string, unknown>;
    if (isStreamingResponse(s.responseBody)) {
      if (provider === "openai") res = parseOpenAIStream(s.responseBody);
      else if (provider === "anthropic") res = parseAnthropicStream(s.responseBody);
      else res = JSON.parse(s.responseBody);
    } else {
      res = JSON.parse(s.responseBody);
    }

    let text: string | undefined;
    if (provider === "gemini") {
      text = res.candidates?.[0]?.content?.parts?.find(
        (p: { text?: string }) => p.text,
      )?.text;
    } else if (provider === "anthropic") {
      text = res.content?.find(
        (b: { type: string; text?: string }) => b.type === "text",
      )?.text;
    } else if (provider === "openai") {
      text = res.choices?.[0]?.message?.content;
    }
    return text?.trim() || null;
  } catch {
    return null;
  }
}

function llmToolCalls(s: ProxySession): string[] {
  const provider = detectLLMProvider(s);
  if (!provider) return [];
  try {
    let res: Record<string, unknown>;
    if (isStreamingResponse(s.responseBody)) {
      if (provider === "openai") res = parseOpenAIStream(s.responseBody);
      else if (provider === "anthropic") res = parseAnthropicStream(s.responseBody);
      else res = JSON.parse(s.responseBody);
    } else {
      res = JSON.parse(s.responseBody);
    }

    if (provider === "gemini") {
      return (
        res.candidates?.[0]?.content?.parts ?? []
      )
        .filter((p: { functionCall?: { name: string } }) => p.functionCall)
        .map((p: { functionCall: { name: string } }) => p.functionCall.name);
    }
    if (provider === "anthropic") {
      return (res.content ?? [])
        .filter((b: { type: string }) => b.type === "tool_use")
        .map((b: { name: string }) => b.name);
    }
    if (provider === "openai") {
      return (res.choices?.[0]?.message?.tool_calls ?? []).map(
        (tc: { function: { name: string } }) => tc.function.name,
      );
    }
    return [];
  } catch {
    return [];
  }
}

function llmToolResults(s: ProxySession): { label: string; snippet: string }[] {
  const provider = detectLLMProvider(s);
  if (!provider) return [];
  try {
    const req = JSON.parse(s.requestBody);
    if (provider === "gemini") {
      // Find the last user turn that contains functionResponse parts
      const contents: { role: string; parts?: unknown[] }[] = req.contents ?? [];
      const lastToolTurn = [...contents]
        .reverse()
        .find(
          (c) =>
            c.role === "user" &&
            (c.parts ?? []).some(
              (p) => (p as { functionResponse?: unknown }).functionResponse,
            ),
        );
      if (!lastToolTurn) return [];
      const parts = (lastToolTurn.parts ?? []) as {
        functionResponse?: { name: string; response: unknown };
      }[];
      return parts
        .filter((p) => p.functionResponse)
        .map((p) => {
          const resp = p.functionResponse!.response;
          const text =
            typeof resp === "string"
              ? resp
              : typeof resp === "object" && resp !== null
                ? (Object.values(resp as Record<string, unknown>).find(
                    (v) => typeof v === "string",
                  ) as string | undefined) ?? JSON.stringify(resp)
                : JSON.stringify(resp);
          return { label: text.slice(0, 60), snippet: text };
        });
    }
    if (provider === "anthropic") {
      // Find the last user message that contains tool_result blocks
      const messages: { role: string; content?: unknown }[] = req.messages ?? [];
      const lastToolMsg = [...messages]
        .reverse()
        .find(
          (m) =>
            m.role === "user" &&
            Array.isArray(m.content) &&
            (m.content as { type: string }[]).some(
              (b) => b.type === "tool_result",
            ),
        );
      if (!lastToolMsg) return [];
      const blocks = (lastToolMsg.content as { type: string; content?: unknown }[]).filter(
        (b) => b.type === "tool_result",
      );
      return blocks.map((b) => {
        const text =
          typeof b.content === "string"
            ? b.content
            : Array.isArray(b.content)
              ? (b.content as { type: string; text?: string }[])
                  .find((c) => c.type === "text")
                  ?.text ?? JSON.stringify(b.content)
              : JSON.stringify(b.content);
        return { label: text.slice(0, 60), snippet: text };
      });
    }
    if (provider === "openai") {
      // Find the last consecutive block of tool messages at the end of messages
      const msgs: { role: string; content?: string }[] = req.messages ?? [];
      const toolMsgs: { role: string; content?: string }[] = [];
      for (let i = msgs.length - 1; i >= 0; i--) {
        if (msgs[i].role === "tool") toolMsgs.unshift(msgs[i]);
        else break;
      }
      return toolMsgs.map((m) => ({
        label: (m.content ?? "").slice(0, 60),
        snippet: m.content ?? "",
      }));
    }
    return [];
  } catch {
    return [];
  }
}

// ── Conversation coloring ─────────────────────────────────

// Colors for conversation groups (soft, VS Code–palette friendly)
const CONV_COLORS = [
  "#569cd6", // blue
  "#4ec9b0", // teal
  "#dcdcaa", // yellow
  "#c586c0", // purple
  "#ce9178", // orange
  "#6a9955", // green
  "#f48771", // red
  "#9cdcfe", // light blue
];

/** Extract a stable fingerprint for the conversation root (first user message). */
function convFingerprint(s: ProxySession): string | null {
  const provider = detectLLMProvider(s);
  if (!provider) return null;
  try {
    const req = JSON.parse(s.requestBody);
    let msgs: unknown[] = [];
    if (provider === "gemini") msgs = req.contents ?? [];
    else msgs = req.messages ?? [];
    if (msgs.length === 0) return null;
    // Use first message as root fingerprint (truncated to avoid huge strings)
    return JSON.stringify(msgs[0]).slice(0, 300);
  } catch {
    return null;
  }
}

/**
 * Map from fingerprint → color string.
 * Only fingerprints appearing on 2+ sessions get a color.
 */
const conversationColorMap = computed(() => {
  // Count occurrences
  const counts = new Map<string, number>();
  for (const s of props.sessions) {
    const fp = convFingerprint(s);
    if (fp) counts.set(fp, (counts.get(fp) ?? 0) + 1);
  }
  // Assign colors in order of first appearance, only for multi-session convos
  const map = new Map<string, string>();
  let idx = 0;
  for (const s of props.sessions) {
    const fp = convFingerprint(s);
    if (fp && (counts.get(fp) ?? 0) >= 2 && !map.has(fp)) {
      map.set(fp, CONV_COLORS[idx % CONV_COLORS.length]);
      idx++;
    }
  }
  return map;
});

function convColor(s: ProxySession): string | null {
  const fp = convFingerprint(s);
  if (!fp) return null;
  return conversationColorMap.value.get(fp) ?? null;
}
</script>

<template>
  <div ref="listEl" class="session-list" tabindex="0">
    <div class="filter-bar">
      <input
        v-model="filterText"
        type="text"
        placeholder="Filter by URL..."
        class="filter-input"
      />
      <label class="llm-filter">
        <input v-model="llmOnly" type="checkbox" />
        <span>LLM requests only</span>
      </label>
    </div>
    <table>
      <thead>
        <tr>
          <th class="col-method" :style="{ width: colWidths.method + 'px' }">
            Method
            <div class="col-resize-handle" @mousedown="startColResize('method', $event)" />
          </th>
          <th class="col-status" :style="{ width: colWidths.status + 'px' }">
            Status
            <div class="col-resize-handle" @mousedown="startColResize('status', $event)" />
          </th>
          <th class="col-url" :style="{ width: colWidths.url + 'px' }">
            URL
            <div class="col-resize-handle" @mousedown="startColResize('url', $event)" />
          </th>
          <th class="col-tools" :style="{ width: colWidths.tools + 'px' }" title="Tool calls">
            Tools
            <div class="col-resize-handle" @mousedown="startColResize('tools', $event)" />
          </th>
          <th class="col-results" :style="{ width: colWidths.results + 'px' }" title="Tool results">
            Results
            <div class="col-resize-handle" @mousedown="startColResize('results', $event)" />
          </th>
          <th class="col-dur" :style="{ width: colWidths.dur + 'px' }">
            ms
          </th>
        </tr>
      </thead>
      <tbody>
        <tr
          v-for="s in filteredSessions"
          :key="s.id"
          :data-id="s.id"
          :class="{ selected: selectedSet.has(s.id) }"
          :style="convColor(s) ? { boxShadow: `inset 3px 0 0 ${convColor(s)}` } : {}"
          @click="handleClick($event, s)"
          @contextmenu="onContextMenu($event, s)"
        >
          <td class="col-method" :class="methodClass(s.method)">
            {{ s.method }}
          </td>
          <td class="col-status" :class="statusClass(s.responseStatus)">
            {{ s.responseStatus }}
          </td>
          <td class="col-url" :title="s.url">
            <span
              v-if="isAutoResponse(s)"
              class="ar-badge"
              title="Auto Responder"
              >⚡</span
            >
            <span v-if="llmPreview(s)" class="llm-preview">{{ llmPreview(s) }}</span>
            <span v-else>{{ s.url }}</span>
          </td>
          <td class="col-tools">
            <span
              v-for="name in llmToolCalls(s)"
              :key="name"
              class="tool-badge"
              :title="name"
            >{{ name }}</span>
          </td>
          <td class="col-results">
            <span
              v-for="(r, i) in llmToolResults(s)"
              :key="i"
              class="result-badge"
              :title="r.snippet"
            >{{ r.label }}</span>
          </td>
          <td class="col-dur">{{ s.durationMs }}</td>
        </tr>
      </tbody>
    </table>
    <div v-if="filteredSessions.length === 0" class="empty-state">
      <template v-if="filterText && sessions.length > 0">
        No sessions match filter "{{ filterText }}".
      </template>
      <template v-else>
        No requests yet.<br />
        Configure your browser/app to use proxy <strong>localhost:8888</strong>
      </template>
    </div>

    <ContextMenu
      v-if="ctxMenu"
      :items="menuItems"
      :x="ctxMenu.x"
      :y="ctxMenu.y"
      @select="onMenuSelect"
      @close="ctxMenu = null"
    />
  </div>
</template>

<style scoped>
.session-list {
  width: 100%;
  overflow-y: auto;
  border-right: none;
  outline: none;
}

.filter-bar {
  background: #252526;
  padding: 8px 10px;
  border-bottom: 1px solid #3e3e42;
  position: sticky;
  top: 0;
  z-index: 10;
  display: flex;
  gap: 10px;
  align-items: center;
}

.filter-input {
  flex: 1;
  background: #3c3c3c;
  border: 1px solid #3e3e42;
  color: #cccccc;
  padding: 6px 10px;
  font-size: 12px;
  border-radius: 4px;
  outline: none;
}

.filter-input:focus {
  border-color: #569cd6;
  background: #1e1e1e;
}

.llm-filter {
  display: flex;
  align-items: center;
  gap: 6px;
  font-size: 12px;
  color: #cccccc;
  white-space: nowrap;
  cursor: pointer;
  user-select: none;
}

.llm-filter input[type="checkbox"] {
  cursor: pointer;
}

table {
  width: 100%;
  border-collapse: collapse;
  font-size: 12px;
  table-layout: fixed;
}

thead th {
  background: #252526;
  color: #858585;
  text-align: left;
  padding: 6px 10px;
  border-bottom: 1px solid #3e3e42;
  position: sticky;
  top: 42px;
  overflow: hidden;
  white-space: nowrap;
}

tbody tr {
  border-bottom: 1px solid #2a2a2a;
  cursor: pointer;
}
tbody tr:hover {
  background: #2a2d2e;
}
tbody tr.selected {
  background: #094771;
}

td {
  padding: 5px 10px;
  white-space: nowrap;
  overflow: hidden;
  max-width: 0;
}

.col-method {
  font-weight: bold;
}
.col-status {
}
.col-url {
  text-overflow: ellipsis;
  overflow: hidden;
}
.col-tools {
  overflow: hidden;
  white-space: nowrap;
  text-overflow: ellipsis;
}
.col-results {
  overflow: hidden;
  white-space: nowrap;
  text-overflow: ellipsis;
}
.col-dur {
  text-align: right;
  color: #858585;
}

.GET {
  color: #4ec9b0;
}
.POST {
  color: #dcdcaa;
}
.PUT {
  color: #ce9178;
}
.DELETE {
  color: #f44747;
}
.PATCH {
  color: #c586c0;
}

.s2 {
  color: #4ec9b0;
}
.s3 {
  color: #9cdcfe;
}
.s4 {
  color: #f44747;
}
.s5 {
  color: #ce9178;
}

.empty-state {
  padding: 40px;
  color: #555;
  text-align: center;
  font-size: 13px;
  line-height: 2;
}

.ar-badge {
  margin-right: 4px;
  font-size: 11px;
}

.col-resize-handle {
  position: absolute;
  right: 0;
  top: 0;
  width: 5px;
  height: 100%;
  cursor: col-resize;
  background: transparent;
}
.col-resize-handle:hover {
  background: #569cd6;
}

.llm-preview {
  color: #ce9178;
  font-style: italic;
}

.tool-badge {
  display: inline-block;
  background: #1a2535;
  color: #569cd6;
  border: 1px solid #2a3a4a;
  border-radius: 3px;
  font-size: 10px;
  padding: 1px 5px;
  margin-right: 3px;
  font-style: normal;
}

.result-badge {
  display: inline-block;
  background: #1e3a2f;
  color: #4ec9b0;
  border: 1px solid #2a4a3a;
  border-radius: 3px;
  font-size: 10px;
  padding: 1px 5px;
  margin-right: 3px;
  font-style: normal;
}
</style>
