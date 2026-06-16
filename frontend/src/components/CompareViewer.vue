<script setup lang="ts">
import { ref, watch, onMounted, onBeforeUnmount } from "vue";
import type { ProxySession } from "../types";
import { MergeView } from "@codemirror/merge";
import { json } from "@codemirror/lang-json";
import { xml } from "@codemirror/lang-xml";
import { oneDark } from "@codemirror/theme-one-dark";
import { EditorView } from "@codemirror/view";

const props = defineProps<{
  a: ProxySession;
  b: ProxySession;
}>();

const emit = defineEmits<{
  close: [];
}>();

const reqHeadersEl = ref<HTMLElement | null>(null);
const reqBodyEl    = ref<HTMLElement | null>(null);
const resHeadersEl = ref<HTMLElement | null>(null);
const resBodyEl    = ref<HTMLElement | null>(null);

const mergeViews: MergeView[] = [];
const showUnchanged = ref(false);

function headersText(h: Record<string, string>): string {
  return Object.entries(h)
    .sort(([a], [b]) => a.localeCompare(b))
    .map(([k, v]) => `${k}: ${v}`)
    .join("\n");
}

type Lang = "json" | "xml" | "text";

function detectLang(s: string): Lang {
  const t = s.trimStart();
  if (t.startsWith("<?xml") || t.startsWith("<")) return "xml";
  if (t.startsWith("{") || t.startsWith("[")) return "json";
  return "text";
}

function prettyJson(s: string): string {
  try { return JSON.stringify(JSON.parse(s), null, 2); }
  catch { return s; }
}

function serializeXmlNode(node: Element, depth: number): string {
  const indent = "  ".repeat(depth);
  const tag = node.tagName;
  const attrs = Array.from(node.attributes).map((a) => ` ${a.name}="${a.value}"`).join("");
  const elementKids = Array.from(node.childNodes).filter((c) => c.nodeType === Node.ELEMENT_NODE) as Element[];
  const text = Array.from(node.childNodes)
    .filter((c) => c.nodeType === Node.TEXT_NODE)
    .map((c) => c.textContent?.trim())
    .filter(Boolean)
    .join("");
  if (elementKids.length === 0)
    return text ? `${indent}<${tag}${attrs}>${text}</${tag}>` : `${indent}<${tag}${attrs}/>`;
  const kids = elementKids.map((c) => serializeXmlNode(c, depth + 1)).join("\n");
  return `${indent}<${tag}${attrs}>\n${kids}\n${indent}</${tag}>`;
}

function prettyXml(s: string): string {
  try {
    const doc = new DOMParser().parseFromString(s.trim(), "application/xml");
    if (doc.querySelector("parsererror")) return s;
    const decl = s.trimStart().startsWith("<?xml") ? s.slice(0, s.indexOf("?>") + 2) + "\n" : "";
    return decl + serializeXmlNode(doc.documentElement, 0);
  } catch {
    return s;
  }
}

function formatBody(s: string): [string, Lang] {
  const lang = detectLang(s);
  if (lang === "json") return [prettyJson(s), "json"];
  if (lang === "xml")  return [prettyXml(s),  "xml"];
  return [s, "text"];
}

const baseTheme = EditorView.theme({
  "&": { fontSize: "12px" },
  ".cm-scroller": {
    fontFamily: "'Cascadia Code','Fira Code','Consolas',monospace",
    overflow: "visible",
  },
  ".cm-editor": { height: "auto" },
});

function buildExts(lang: Lang) {
  const langExt = lang === "json" ? [json()] : lang === "xml" ? [xml()] : [];
  return [oneDark, baseTheme, EditorView.editable.of(false), ...langExt];
}

function mount(el: HTMLElement, docA: string, docB: string, lang: Lang = "text") {
  const mv = new MergeView({
    a: { doc: docA, extensions: buildExts(lang) },
    b: { doc: docB, extensions: buildExts(lang) },
    parent: el,
    collapseUnchanged: showUnchanged.value ? undefined : { margin: 3, minSize: 4 },
  });
  mergeViews.push(mv);
}

function collapseConfig() {
  return showUnchanged.value ? undefined : { margin: 3, minSize: 4 };
}

onMounted(() => {
  if (reqHeadersEl.value)
    mount(reqHeadersEl.value, headersText(props.a.requestHeaders), headersText(props.b.requestHeaders));
  if (reqBodyEl.value) {
    const [fA, lang] = formatBody(props.a.requestBody);
    const [fB]       = formatBody(props.b.requestBody);
    mount(reqBodyEl.value, fA, fB, lang);
  }
  if (resHeadersEl.value)
    mount(resHeadersEl.value, headersText(props.a.responseHeaders), headersText(props.b.responseHeaders));
  if (resBodyEl.value) {
    const [fA, lang] = formatBody(props.a.responseBody);
    const [fB]       = formatBody(props.b.responseBody);
    mount(resBodyEl.value, fA, fB, lang);
  }
});

watch(showUnchanged, () => {
  const cfg = { collapseUnchanged: collapseConfig() };
  mergeViews.forEach((mv) => mv.reconfigure(cfg));
});

onBeforeUnmount(() => {
  mergeViews.forEach((mv) => mv.destroy());
  document.removeEventListener("keydown", onKeyDown);
});

function onKeyDown(e: KeyboardEvent) {
  if (e.key === "Escape") emit("close");
}
onMounted(() => document.addEventListener("keydown", onKeyDown));

const methodClass = (method: string) => {
  switch (method) {
    case "GET":    return "method-get";
    case "POST":   return "method-post";
    case "PUT":
    case "PATCH":  return "method-put";
    case "DELETE": return "method-delete";
    default:       return "method-other";
  }
};
</script>

<template>
  <div class="backdrop" @click.self="emit('close')">
    <div class="card">

      <!-- Header -->
      <div class="card-header">
        <div class="sessions">
          <div class="session-label label-a">
            <span class="side-tag">A</span>
            <span class="method" :class="methodClass(a.method)">{{ a.method }}</span>
            <span class="url" :title="a.url">{{ a.url }}</span>
            <span class="status">{{ a.responseStatus }}</span>
          </div>
          <div class="vs">vs</div>
          <div class="session-label label-b">
            <span class="side-tag">B</span>
            <span class="method" :class="methodClass(b.method)">{{ b.method }}</span>
            <span class="url" :title="b.url">{{ b.url }}</span>
            <span class="status">{{ b.responseStatus }}</span>
          </div>
        </div>
        <label class="toggle">
          <input type="checkbox" v-model="showUnchanged" />
          Show unchanged
        </label>
        <button class="close-btn" @click="emit('close')">✕</button>
      </div>

      <!-- Scrollable sections -->
      <div class="scroll-area">

        <div class="section">
          <div class="section-title">Request</div>

          <div class="subsection-title">Headers</div>
          <div ref="reqHeadersEl" class="diff-block" />

          <div class="subsection-title">Body</div>
          <div ref="reqBodyEl" class="diff-block" />
        </div>

        <div class="section">
          <div class="section-title">Response</div>

          <div class="subsection-title">Headers</div>
          <div ref="resHeadersEl" class="diff-block" />

          <div class="subsection-title">Body</div>
          <div ref="resBodyEl" class="diff-block" />
        </div>

      </div>
    </div>
  </div>
</template>

<style scoped>
.backdrop {
  position: fixed;
  inset: 0;
  background: rgba(0, 0, 0, 0.72);
  z-index: 1000;
  display: flex;
  align-items: center;
  justify-content: center;
}

.card {
  background: #1e1e1e;
  border: 1px solid #3e3e42;
  border-radius: 6px;
  width: 95vw;
  height: 92vh;
  display: flex;
  flex-direction: column;
  overflow: hidden;
}

/* ── Header ─────────────────────────────── */
.card-header {
  display: flex;
  align-items: center;
  padding: 8px 14px;
  border-bottom: 1px solid #3e3e42;
  flex-shrink: 0;
  gap: 12px;
  min-width: 0;
}

.sessions {
  display: flex;
  align-items: center;
  gap: 10px;
  flex: 1;
  min-width: 0;
  overflow: hidden;
}

.session-label {
  display: flex;
  align-items: center;
  gap: 6px;
  flex: 1;
  min-width: 0;
  overflow: hidden;
}

.side-tag {
  font-size: 10px;
  font-weight: bold;
  padding: 1px 5px;
  border-radius: 3px;
  flex-shrink: 0;
}
.label-a .side-tag { background: #1e2a3f; color: #569cd6; }
.label-b .side-tag { background: #1e3a2f; color: #4ec9b0; }

.url {
  font-size: 11px;
  color: #d4d4d4;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}
.status { font-size: 11px; color: #858585; flex-shrink: 0; }
.vs     { font-size: 11px; color: #555;    flex-shrink: 0; }

.method {
  font-size: 10px;
  font-weight: bold;
  padding: 1px 5px;
  border-radius: 3px;
  flex-shrink: 0;
}
.method-get    { background: #1e3a2f; color: #4ec9b0; }
.method-post   { background: #1e2a3f; color: #569cd6; }
.method-put    { background: #3a2e1e; color: #dcdcaa; }
.method-delete { background: #3a1e1e; color: #f44747; }
.method-other  { background: #2d2d2d; color: #d4d4d4; }

.toggle {
  display: flex;
  align-items: center;
  gap: 5px;
  font-size: 11px;
  color: #858585;
  cursor: pointer;
  flex-shrink: 0;
  user-select: none;
}
.toggle:hover { color: #d4d4d4; }
.toggle input { cursor: pointer; accent-color: #007acc; }

.close-btn {
  background: none;
  border: none;
  color: #858585;
  cursor: pointer;
  font-size: 16px;
  flex-shrink: 0;
  padding: 4px 8px;
  font-family: inherit;
}
.close-btn:hover { color: #d4d4d4; }

/* ── Scroll area ─────────────────────────── */
.scroll-area {
  flex: 1;
  overflow-y: auto;
  min-height: 0;
}

.section {
  border-bottom: 2px solid #3e3e42;
  padding-bottom: 8px;
}

.section-title {
  padding: 10px 14px 6px;
  font-size: 11px;
  font-weight: bold;
  color: #569cd6;
  text-transform: uppercase;
  letter-spacing: 0.08em;
  background: #252526;
  border-bottom: 1px solid #3e3e42;
  position: sticky;
  top: 0;
  z-index: 1;
}

.subsection-title {
  padding: 8px 14px 4px;
  font-size: 10px;
  color: #858585;
  text-transform: uppercase;
  letter-spacing: 0.06em;
}

.diff-block {
  margin: 0 0 4px;
}

/* ── CodeMirror overrides ────────────────── */
.diff-block :deep(.cm-mergeView) {
  width: 100%;
}
.diff-block :deep(.cm-mergeViewEditors) {
  display: flex;
  width: 100%;
}
.diff-block :deep(.cm-editor) {
  flex: 1;
  height: auto;
  min-width: 0;
}
.diff-block :deep(.cm-scroller) {
  overflow: visible;
}
.diff-block :deep(.cm-deletedChunk)  { background: rgba(244, 71, 71, 0.15); }
.diff-block :deep(.cm-changedLine)   { background: rgba(220, 220, 100, 0.08); }
.diff-block :deep(.cm-insertedLine),
.diff-block :deep(.cm-changedText)   { background: rgba(78, 201, 176, 0.15); }
</style>
