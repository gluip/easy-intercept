<script setup lang="ts">
import { ref, computed, watch, onMounted, onUnmounted } from "vue";
import type { ProxySession } from "../types";
import ContextMenu, { type MenuItem } from "./ContextMenu.vue";
import { detectLLMProvider } from "../utils/llm-detection";
import { isStreamingResponse, parseOpenAIStream, parseAnthropicStream, parseCopilotResponsesStream, isOpenAIResponsesRequest, parseOpenAIResponses } from "../utils/llm-stream-parser";
import { isElasticsearchRequest, detectESOperation, parseESIndex } from "../utils/es-detection";
import { isGraphQLRequest, parseGraphQLRequest, parseGraphQLResponse, getOperationName, getOperationType, getTracingDurationMs } from "../utils/graphql-detection";
import { calcCost, formatCost } from "../utils/llm-cost";
import { extractLLMUsage } from "../utils/llm-usage";
import { isGeminiInteractionsRequest, parseGeminiInteractionsResponse, geminiInteractionsPreviewText, geminiInteractionsToolCallNames, geminiInteractionsTrailingResults } from "../utils/gemini-interactions";
import { detectRequestKind, REQUEST_KIND_LABELS, REQUEST_KIND_ICONS, type RequestKind } from "../utils/request-kind-detection";

const props = defineProps<{
  sessions: readonly ProxySession[];
  selectedIds: string[];
}>();

const emit = defineEmits<{
  select: [ids: string[]];
  copyUrl: [session: ProxySession];
  replay: [session: ProxySession];
  deleteSelected: [ids: string[]];
  compare: [ids: [string, string]];
}>();

const listEl = ref<HTMLElement>();
const lastClickedId = ref<string | null>(null);
const filterText = ref("");
const llmOnly = ref(false);
const kindFilter = ref<RequestKind | "all">("all");
const timelineMode = ref(false);

const ctxMenu = ref<{ session: ProxySession; x: number; y: number } | null>(
  null,
);

// Mark colours — persisted per session id
const MARK_COLORS = [
  { name: "Red", value: "#f44747" },
  { name: "Orange", value: "#dc8a3a" },
  { name: "Yellow", value: "#dcdcaa" },
  { name: "Green", value: "#4ec9b0" },
  { name: "Blue", value: "#569cd6" },
  { name: "Purple", value: "#c586c0" },
  { name: "Clear", value: "" },
];

const MARKS_KEY = "easyintercept.marks";

function loadMarks(): Record<string, string> {
  try {
    return JSON.parse(localStorage.getItem(MARKS_KEY) ?? "{}");
  } catch {
    return {};
  }
}

const marks = ref<Record<string, string>>(loadMarks());

// Hideable fixed columns — persisted per browser
type FixedColKey = "method" | "status" | "host" | "url" | "dur";
const COLUMN_LABELS: Record<FixedColKey, string> = {
  method: "Method", status: "Status", host: "Host", url: "URL", dur: "ms",
};
const VISIBLE_COLS_KEY = "easyintercept.visibleColumns";

function loadVisibleCols(): Record<FixedColKey, boolean> {
  const defaults: Record<FixedColKey, boolean> = { method: true, status: true, host: true, url: true, dur: true };
  try {
    return { ...defaults, ...JSON.parse(localStorage.getItem(VISIBLE_COLS_KEY) ?? "{}") };
  } catch {
    return defaults;
  }
}

const visibleCols = ref<Record<FixedColKey, boolean>>(loadVisibleCols());

function toggleColumn(col: FixedColKey) {
  const next = { ...visibleCols.value, [col]: !visibleCols.value[col] };
  if (Object.values(next).some(Boolean)) {
    visibleCols.value = next;
    localStorage.setItem(VISIBLE_COLS_KEY, JSON.stringify(next));
  }
}

const columnsMenuOpen = ref(false);
const columnsMenuPos = ref<{ x: number; y: number } | null>(null);
const columnsMenuEl = ref<HTMLElement>();

function openColumnsMenuAtCursor(e: MouseEvent) {
  e.preventDefault();
  columnsMenuPos.value = { x: e.clientX, y: e.clientY };
  columnsMenuOpen.value = true;
}

function handleColumnsOutside(e: MouseEvent) {
  if (columnsMenuEl.value && !columnsMenuEl.value.contains(e.target as Node)) {
    columnsMenuOpen.value = false;
  }
}

function setMark(id: string, color: string) {
  if (color) marks.value[id] = color;
  else delete marks.value[id];
  localStorage.setItem(MARKS_KEY, JSON.stringify(marks.value));
}

function markColor(s: ProxySession): string | null {
  return marks.value[s.id] || null;
}

function hexToRgba(hex: string, alpha: number): string {
  const r = parseInt(hex.slice(1, 3), 16);
  const g = parseInt(hex.slice(3, 5), 16);
  const b = parseInt(hex.slice(5, 7), 16);
  return `rgba(${r}, ${g}, ${b}, ${alpha})`;
}

function rowStyle(s: ProxySession): Record<string, string> {
  const style: Record<string, string> = {};
  const mc = markColor(s);
  const cc = convColor(s);
  const shadows: string[] = [];
  if (mc) shadows.push(`inset 4px 0 0 ${mc}`);
  else if (cc) shadows.push(`inset 3px 0 0 ${cc}`);
  if (shadows.length) style.boxShadow = shadows.join(", ");
  if (mc) style.backgroundColor = hexToRgba(mc, 0.35);
  else if (detectRequestKind(s) === "asset") style.opacity = "0.55";
  return style;
}

const selectedSet = computed(() => new Set(props.selectedIds));

const filteredSessions = computed(() => {
  let result = props.sessions;
  
  // Filter by LLM only
  if (llmOnly.value) {
    result = result.filter((s) => detectLLMProvider(s) !== null);
  }

  // Filter by request kind
  if (kindFilter.value !== "all") {
    result = result.filter((s) => detectRequestKind(s) === kindFilter.value);
  }

  // Filter by URL text
  const needle = filterText.value.toLowerCase().trim();
  if (needle) {
    result = result.filter((s) =>
      s.url.toLowerCase().includes(needle) ||
      s.requestBody.toLowerCase().includes(needle) ||
      s.responseBody.toLowerCase().includes(needle)
    );
  }

  // Chronological order in timeline mode — newest on top, oldest at the
  // bottom, matching the default (non-timeline) list order so the rows
  // don't visually flip when the toggle is switched on.
  if (timelineMode.value) {
    result = [...result].sort(
      (a, b) => new Date(b.timestamp).getTime() - new Date(a.timestamp).getTime(),
    );
  }

  return result;
});

const timelineRange = computed(() => {
  if (!timelineMode.value || filteredSessions.value.length === 0) return null;
  const starts = filteredSessions.value.map((s) => new Date(s.timestamp).getTime());
  const ends = filteredSessions.value.map((s) => {
    const start = new Date(s.timestamp).getTime();
    const dur = s.responseStatus === 0 ? Date.now() - start : s.durationMs;
    return start + Math.max(dur, 0);
  });
  const min = Math.min(...starts);
  const max = Math.max(...ends);
  return { min, span: Math.max(max - min, 1) };
});

function timelineBarStyle(s: ProxySession): Record<string, string> {
  const range = timelineRange.value;
  if (!range) return {};
  const start = new Date(s.timestamp).getTime();
  const dur = s.responseStatus === 0 ? Date.now() - start : s.durationMs;
  const left = ((start - range.min) / range.span) * 100;
  const width = Math.max((Math.max(dur, 0) / range.span) * 100, 0.5);
  return { left: left + "%", width: width + "%" };
}

const menuItems = computed(() => {
  const items: MenuItem[] = [
    { label: "Copy URL", icon: "📋", action: "copy-url" },
    { label: "Copy file path", icon: "📄", action: "copy-file-path" },
    { label: "Show in Explorer", icon: "📂", action: "show-in-explorer" },
    { label: "Replay", icon: "🔁", action: "replay" },
    { label: "Add to Bruno", icon: "🐶", action: "add-to-bruno" },
    { label: "Mark", icon: "🎨", action: "mark", colors: MARK_COLORS },
    { label: "Delete", icon: "🗑️", action: "delete" },
  ];
  if (props.selectedIds.length === 2) {
    items.splice(6, 0, { label: "Compare", icon: "⚖️", action: "compare" });
  }
  return items;
});

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
  else if (action === "copy-file-path") {
    fetch(`/api/sessions/${session.id}/file-path`)
      .then((r) => r.json())
      .then((d) => navigator.clipboard.writeText(d.path))
      .catch(() => {});
  }
  else if (action === "show-in-explorer") {
    fetch(`/api/sessions/${session.id}/show-in-explorer`, { method: "POST" }).catch(() => {});
  }
  else if (action === "replay") emit("replay", session);
  else if (action === "add-to-bruno") {
    const ids = props.selectedIds.includes(session.id) ? [...props.selectedIds] : [session.id];
    exportToBruno(session, ids);
  }
  else if (action === "delete") emit("deleteSelected", [...props.selectedIds]);
  else if (action === "compare" && props.selectedIds.length === 2)
    emit("compare", [props.selectedIds[0], props.selectedIds[1]]);
  else if (action.startsWith("mark:")) {
    const color = action.slice("mark:".length);
    const ids = props.selectedIds.includes(session.id) ? props.selectedIds : [session.id];
    ids.forEach((id) => setMark(id, color));
  }
}

const BRUNO_PATH_KEY = "bruno-collection-path";

function defaultBrunoName(session: ProxySession): string {
  try {
    return `${session.method} ${new URL(session.url).pathname}`;
  } catch {
    return `${session.method} ${session.url}`;
  }
}

async function exportToBruno(session: ProxySession, sessionIds: string[]) {
  let name: string | null = null;
  if (sessionIds.length === 1) {
    const target = props.sessions.find((s) => s.id === sessionIds[0]) ?? session;
    const input = window.prompt("Bruno request name:", defaultBrunoName(target));
    if (input === null) return;
    name = input.trim() || null;
  }
  const lastPath = localStorage.getItem(BRUNO_PATH_KEY) ?? "";
  const path = window.prompt("Bruno collection folder:", lastPath);
  if (!path?.trim()) return;
  localStorage.setItem(BRUNO_PATH_KEY, path.trim());
  try {
    const resp = await fetch("/api/bruno/export", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ sessionIds, collectionPath: path.trim(), name }),
    });
    if (!resp.ok) {
      let message = await resp.text();
      try { message = JSON.parse(message); } catch { /* plain text */ }
      alert(`Export to Bruno failed: ${message}`);
    }
  } catch (e) {
    alert(`Export to Bruno failed: ${e}`);
  }
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
      filteredSessions.value.map((s) => s.id),
    );
  }
  if (e.key === "ArrowDown" || e.key === "ArrowUp") {
    e.preventDefault();
    const ids = filteredSessions.value.map((s) => s.id);
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

watch(columnsMenuOpen, (open) => {
  if (open) setTimeout(() => document.addEventListener("mousedown", handleColumnsOutside), 0);
  else document.removeEventListener("mousedown", handleColumnsOutside);
});

function methodClass(m: string) {
  return ["GET", "POST", "PUT", "DELETE", "PATCH"].includes(m) ? m : "";
}

function statusClass(s: number) {
  if (s === 0) return "pending";
  if (s >= 500) return "s5";
  if (s >= 400) return "s4";
  if (s >= 300) return "s3";
  return "s2";
}

function isAutoResponse(s: ProxySession) {
  return s.responseHeaders?.["X-EasyIntercept-AutoResponder"] === "true";
}

function hostOf(url: string): string {
  try { return new URL(url).host; } catch { return ""; }
}
function pathOf(url: string): string {
  try { const u = new URL(url); return u.pathname + u.search; } catch { return url; }
}

// ── Column resize ─────────────────────────────────────────

type ColKey = "method" | "status" | "host" | "url" | "tools" | "results" | "dur" | "cost" | "timeline";

const colWidths = ref<Record<ColKey, number>>({
  method: 62,
  status: 46,
  host: 140,
  url: 200,
  tools: 130,
  results: 320,
  dur: 56,
  cost: 80,
  timeline: 160,
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
    if (provider === "gemini" && isGeminiInteractionsRequest(s.requestBody)) {
      const r = parseGeminiInteractionsResponse(s.responseBody);
      return r ? geminiInteractionsPreviewText(r.steps)?.trim() || null : null;
    }

    if (provider === "openai" && isOpenAIResponsesRequest(s.requestBody)) {
      const r = parseOpenAIResponses(s.responseBody);
      const msg = r.output.find((o) => o.type === "message") as { content?: { type: string; text?: string }[] } | undefined;
      const text = msg?.content?.find((c) => c.type === "output_text")?.text;
      return text?.trim() || null;
    }

    let res: any;
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
    } else if (provider === "copilot") {
      const parsed = parseCopilotResponsesStream(s.responseBody);
      text = parsed.output.find((o) => o.type === "message")
        ?.content?.find((c) => c.type === "output_text")?.text;
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
    if (provider === "gemini" && isGeminiInteractionsRequest(s.requestBody)) {
      const r = parseGeminiInteractionsResponse(s.responseBody);
      return r ? geminiInteractionsToolCallNames(r.steps) : [];
    }

    if (provider === "openai" && isOpenAIResponsesRequest(s.requestBody)) {
      const r = parseOpenAIResponses(s.responseBody);
      return r.output
        .filter((o) => o.type === "function_call")
        .map((o) => o.name as string);
    }

    let res: any;
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
    if (provider === "gemini" && isGeminiInteractionsRequest(s.requestBody)) {
      // Trailing run of function_result items at the end of input
      return geminiInteractionsTrailingResults(req.input ?? []).map((text) => ({
        label: text.slice(0, 60),
        snippet: text,
      }));
    }
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
    if (provider === "openai" && isOpenAIResponsesRequest(s.requestBody)) {
      // Find the last consecutive block of function_call_output items at the end of input
      const items: { type?: string; output?: string }[] = req.input ?? [];
      const toolItems: { type?: string; output?: string }[] = [];
      for (let i = items.length - 1; i >= 0; i--) {
        if (items[i].type === "function_call_output") toolItems.unshift(items[i]);
        else break;
      }
      return toolItems.map((m) => ({
        label: (m.output ?? "").slice(0, 60),
        snippet: m.output ?? "",
      }));
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
    if (provider === "gemini" && isGeminiInteractionsRequest(s.requestBody)) msgs = req.input ?? [];
    else if (provider === "gemini") msgs = req.contents ?? [];
    else if (provider === "openai" && isOpenAIResponsesRequest(s.requestBody)) msgs = req.input ?? [];
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

function esPreview(s: ProxySession): string | null {
  if (!isElasticsearchRequest(s)) return null;
  const op = detectESOperation(s);
  const idx = parseESIndex(s.url);
  try {
    const resp = JSON.parse(s.responseBody);
    if (op === "search") {
      const total = resp?.hits?.total?.value;
      const took = resp?.took;
      const parts = [idx || "_search"];
      if (total !== undefined) parts.push(`${total} hits`);
      if (took !== undefined) parts.push(`${took}ms`);
      return parts.join("  ·  ");
    }
    if (op === "pit-create") return `pit  ·  ${idx || "create"}`;
    if (op === "pit-delete") return `pit delete  ·  freed ${resp?.num_freed ?? "?"}`;
    if (op === "bulk") return `bulk  ·  ${idx}`;
    if (op === "bulk-log") return `logs  ·  ${idx}`;
  } catch { /* fall through */ }
  return idx ? `${op}  ·  ${idx}` : null;
}

function graphqlPreview(s: ProxySession): string | null {
  if (!isGraphQLRequest(s)) return null;
  const ops = parseGraphQLRequest(s);
  if (!ops || ops.length === 0) return null;
  const names = ops.map((op) => `${getOperationType(op.query)} ${getOperationName(op) ?? "?"}`);
  if (ops.length === 1) {
    const tracingMs = getTracingDurationMs(parseGraphQLResponse(s)?.[0]);
    if (tracingMs !== null) return `${names[0]} · ${Math.round(tracingMs)}ms`;
  }
  return names.join(", ");
}

function llmCost(s: ProxySession): string | null {
  const provider = detectLLMProvider(s);
  const usage = extractLLMUsage(s);
  if (!provider || !usage) return null;
  const cost = calcCost(
    provider,
    usage.model,
    usage.promptTokens,
    usage.responseTokens,
    usage.cachedTokens,
    usage.thoughtTokens,
  );
  return cost ? formatCost(cost) : null;
}
</script>

<template>
  <div ref="listEl" class="session-list" tabindex="0">
    <div class="filter-bar">
      <div class="filter-input-wrap">
        <input
          v-model="filterText"
          type="text"
          placeholder="Filter by URL, request or response body..."
          class="filter-input"
        />
        <button
          v-if="filterText"
          class="filter-clear"
          title="Clear filter"
          @click="filterText = ''"
        >
          ✕
        </button>
      </div>
      <label class="llm-filter">
        <input v-model="llmOnly" type="checkbox" />
        <span>LLM requests only</span>
      </label>
      <select v-model="kindFilter" class="kind-filter" title="Filter by request type">
        <option value="all">All types</option>
        <option v-for="k in (['document', 'asset', 'browser-api', 'backend'] as RequestKind[])" :key="k" :value="k">
          {{ REQUEST_KIND_ICONS[k] }} {{ REQUEST_KIND_LABELS[k] }}
        </option>
      </select>
      <label class="llm-filter">
        <input v-model="timelineMode" type="checkbox" />
        <span>⏱ Timeline</span>
      </label>
      <div ref="columnsMenuEl">
        <div
          v-if="columnsMenuOpen"
          class="columns-menu"
          :style="{ left: (columnsMenuPos?.x ?? 0) + 'px', top: (columnsMenuPos?.y ?? 0) + 'px' }"
        >
          <label v-for="(label, key) in COLUMN_LABELS" :key="key">
            <input type="checkbox" :checked="visibleCols[key]" @change="toggleColumn(key)" />
            <span>{{ label }}</span>
          </label>
        </div>
      </div>
    </div>
    <table>
      <thead @contextmenu="openColumnsMenuAtCursor">
        <tr>
          <th v-if="visibleCols.method" class="col-method" :style="{ width: colWidths.method + 'px' }">
            Method
            <div class="col-resize-handle" @mousedown="startColResize('method', $event)" />
          </th>
          <th v-if="visibleCols.status" class="col-status" :style="{ width: colWidths.status + 'px' }">
            Status
            <div class="col-resize-handle" @mousedown="startColResize('status', $event)" />
          </th>
          <th v-if="visibleCols.host" class="col-host" :style="{ width: colWidths.host + 'px' }">
            Host
            <div class="col-resize-handle" @mousedown="startColResize('host', $event)" />
          </th>
          <th v-if="visibleCols.url" class="col-url" :style="{ width: colWidths.url + 'px' }">
            URL
            <div class="col-resize-handle" @mousedown="startColResize('url', $event)" />
          </th>
          <th v-if="llmOnly" class="col-tools" :style="{ width: colWidths.tools + 'px' }" title="Tool calls">
            Tools
            <div class="col-resize-handle" @mousedown="startColResize('tools', $event)" />
          </th>
          <th v-if="llmOnly" class="col-results" :style="{ width: colWidths.results + 'px' }" title="Tool results">
            Results
            <div class="col-resize-handle" @mousedown="startColResize('results', $event)" />
          </th>
          <th v-if="visibleCols.dur" class="col-dur" :style="{ width: colWidths.dur + 'px' }">
            ms
          </th>
          <th v-if="llmOnly" class="col-cost" :style="{ width: colWidths.cost + 'px' }">
            cost
            <div class="col-resize-handle" @mousedown="startColResize('cost', $event)" />
          </th>
          <th v-if="timelineMode" class="col-timeline" :style="{ width: colWidths.timeline + 'px' }">
            Timeline
            <div class="col-resize-handle" @mousedown="startColResize('timeline', $event)" />
          </th>
        </tr>
      </thead>
      <tbody>
        <tr
          v-for="s in filteredSessions"
          :key="s.id"
          :data-id="s.id"
          :class="{ selected: selectedSet.has(s.id) }"
          :style="rowStyle(s)"
          @click="handleClick($event, s)"
          @contextmenu="onContextMenu($event, s)"
        >
          <td v-if="visibleCols.method" class="col-method" :class="methodClass(s.method)">
            {{ s.method }}
          </td>
          <td v-if="visibleCols.status" class="col-status" :class="statusClass(s.responseStatus)">
            <span v-if="s.responseStatus === 0" class="pending-dots">···</span>
            <template v-else>{{ s.responseStatus }}</template>
          </td>
          <td v-if="visibleCols.host" class="col-host" :title="hostOf(s.url)">{{ hostOf(s.url) }}</td>
          <td v-if="visibleCols.url" class="col-url" :title="s.url">
            <span
              v-if="isAutoResponse(s)"
              class="ar-badge"
              title="Auto Responder"
              >⚡</span
            >
            <span
              class="kind-badge"
              :class="'kind-' + detectRequestKind(s)"
              :title="REQUEST_KIND_LABELS[detectRequestKind(s)]"
              >{{ REQUEST_KIND_ICONS[detectRequestKind(s)] }}</span
            >
            <span v-if="llmPreview(s)" class="llm-preview">{{ llmPreview(s) }}</span>
            <span v-else-if="esPreview(s)" class="es-preview">{{ esPreview(s) }}</span>
            <span v-else-if="graphqlPreview(s)" class="gql-preview">{{ graphqlPreview(s) }}</span>
            <span v-else>{{ pathOf(s.url) }}</span>
          </td>
          <td v-if="llmOnly" class="col-tools">
            <span
              v-for="name in llmToolCalls(s)"
              :key="name"
              class="tool-badge"
              :title="name"
            >{{ name }}</span>
          </td>
          <td v-if="llmOnly" class="col-results">
            <span
              v-for="(r, i) in llmToolResults(s)"
              :key="i"
              class="result-badge"
              :title="r.snippet"
            >{{ r.label }}</span>
          </td>
          <td v-if="visibleCols.dur" class="col-dur">{{ s.responseStatus === 0 ? "" : s.durationMs }}</td>
          <td v-if="llmOnly" class="col-cost">{{ s.responseStatus === 0 ? "" : (llmCost(s) ?? "") }}</td>
          <td v-if="timelineMode" class="col-timeline">
            <div class="timeline-track">
              <span
                class="timeline-bar"
                :class="'kind-' + detectRequestKind(s)"
                :style="timelineBarStyle(s)"
                :title="`${new Date(s.timestamp).toLocaleTimeString()} · ${s.responseStatus === 0 ? 'pending' : s.durationMs + 'ms'}`"
              />
            </div>
          </td>
        </tr>
      </tbody>
    </table>
    <div v-if="filteredSessions.length === 0" class="empty-state">
      <template v-if="filterText && sessions.length > 0">
        No sessions match filter "{{ filterText }}".
      </template>
      <template v-else>
        No requests yet.<br />
        Configure your browser/app to use proxy <strong>localhost:9999</strong>
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

.filter-input-wrap {
  position: relative;
  flex: 1;
  display: flex;
}

.filter-input {
  flex: 1;
  background: #3c3c3c;
  border: 1px solid #3e3e42;
  color: #cccccc;
  padding: 6px 28px 6px 10px;
  font-size: 12px;
  border-radius: 4px;
  outline: none;
  width: 100%;
}

.filter-clear {
  position: absolute;
  right: 4px;
  top: 50%;
  transform: translateY(-50%);
  background: none;
  border: none;
  color: #858585;
  cursor: pointer;
  font-size: 12px;
  padding: 2px 6px;
  line-height: 1;
}
.filter-clear:hover {
  color: #d4d4d4;
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

.kind-filter {
  font-size: 12px;
  color: #cccccc;
  background: #1e1e1e;
  border: 1px solid #3e3e42;
  border-radius: 3px;
  padding: 3px 6px;
  cursor: pointer;
}

table {
  width: 100%;
  border-collapse: collapse;
  font-size: 12px;
  table-layout: fixed;
  user-select: none;
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

.col-cost {
  text-align: right;
  color: #4ec9b0;
  font-size: 11px;
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

.pending {
  color: #858585;
}

.pending-dots {
  display: inline-block;
  animation: pending-pulse 1.2s ease-in-out infinite;
}

@keyframes pending-pulse {
  0%, 100% { opacity: 0.3; }
  50% { opacity: 1; }
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

.kind-badge {
  margin-right: 4px;
  font-size: 10px;
  opacity: 0.8;
}

.kind-badge.kind-asset {
  opacity: 0.5;
}

.columns-menu {
  position: fixed;
  z-index: 1000;
  background: #252526;
  border: 1px solid #3e3e42;
  border-radius: 4px;
  box-shadow: 0 4px 12px rgba(0, 0, 0, 0.5);
  padding: 4px 0;
  min-width: 120px;
}
.columns-menu label {
  display: flex;
  align-items: center;
  gap: 6px;
  padding: 5px 12px;
  font-size: 12px;
  color: #d4d4d4;
  cursor: pointer;
}
.columns-menu label:hover {
  background: #094771;
}
.col-host {
  color: #858585;
  text-overflow: ellipsis;
  overflow: hidden;
  white-space: nowrap;
}

.timeline-track {
  position: relative;
  height: 14px;
  background: #1e1e1e;
  border-radius: 2px;
  overflow: hidden;
}
.timeline-bar {
  position: absolute;
  top: 1px;
  height: 12px;
  border-radius: 2px;
  min-width: 2px;
}
.timeline-bar.kind-document {
  background: #569cd6;
}
.timeline-bar.kind-asset {
  background: #6a6a6a;
}
.timeline-bar.kind-browser-api {
  background: #4ec9b0;
}
.timeline-bar.kind-backend {
  background: #dc8a3a;
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

.es-preview {
  color: #4ec9b0;
  font-style: italic;
}

.gql-preview {
  color: #9cdcfe;
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
