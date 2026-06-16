<script setup lang="ts">
import { ref, computed, onMounted, onBeforeUnmount } from "vue";
import type { ProxySession } from "../types";
import JsonTree from "./JsonTree.vue";

const props = defineProps<{
  session: ProxySession;
  initialTab?: "request" | "response";
}>();

const emit = defineEmits<{
  close: [];
}>();

const tab = ref<"request" | "response">(props.initialTab ?? "request");

// Headers sections collapsed by default
const reqHeadersOpen = ref(false);
const resHeadersOpen = ref(false);

// Raw vs Tree mode per tab
const reqRawMode = ref(false);
const resRawMode = ref(false);

// Tree remount keys + forceOpen for expand/collapse all
const reqTreeKey = ref(0);
const resTreeKey = ref(0);
const reqForceOpen = ref<boolean | undefined>(undefined);
const resForceOpen = ref<boolean | undefined>(undefined);

function reqExpandAll() {
  reqForceOpen.value = true;
  reqTreeKey.value++;
}
function reqCollapseAll() {
  reqForceOpen.value = false;
  reqTreeKey.value++;
}
function resExpandAll() {
  resForceOpen.value = true;
  resTreeKey.value++;
}
function resCollapseAll() {
  resForceOpen.value = false;
  resTreeKey.value++;
}

// Parse bodies — handles both regular JSON and unicode-escaped JSON (\u0022 etc.)
function tryParse(s: string): unknown | null {
  if (!s) return null;
  try {
    return JSON.parse(s);
  } catch {
    return null;
  }
}

const parsedReqBody = computed(() => tryParse(props.session.requestBody));
const parsedResBody = computed(() => tryParse(props.session.responseBody));

function getHeader(headers: Record<string, string>, name: string): string {
  const key = Object.keys(headers).find(k => k.toLowerCase() === name.toLowerCase());
  return key ? headers[key] : '';
}

const resContentType = computed(() => getHeader(props.session.responseHeaders, 'content-type').toLowerCase());

const isResponseImage = computed(() =>
  props.session.responseBody.startsWith('data:image/') || resContentType.value.includes('image/')
);

const responseImageSrc = computed(() => {
  const body = props.session.responseBody;
  if (body.startsWith('data:image/')) return body;
  if (resContentType.value.includes('svg'))
    return `data:image/svg+xml;charset=utf-8,${encodeURIComponent(body)}`;
  return '';
});

// Copy body to clipboard
const reqCopied = ref(false);
const resCopied = ref(false);

function copyReqBody() {
  const text =
    parsedReqBody.value !== null
      ? JSON.stringify(parsedReqBody.value, null, 2)
      : props.session.requestBody;
  navigator.clipboard.writeText(text);
  reqCopied.value = true;
  setTimeout(() => (reqCopied.value = false), 1500);
}

function copyResBody() {
  const text =
    parsedResBody.value !== null
      ? JSON.stringify(parsedResBody.value, null, 2)
      : props.session.responseBody;
  navigator.clipboard.writeText(text);
  resCopied.value = true;
  setTimeout(() => (resCopied.value = false), 1500);
}

// Headers as entry arrays
const reqHeaders = computed(() =>
  Object.entries(props.session.requestHeaders),
);
const resHeaders = computed(() =>
  Object.entries(props.session.responseHeaders),
);

// Method + status colour helpers
const methodClass = computed(() => {
  switch (props.session.method) {
    case "GET":
      return "method-get";
    case "POST":
      return "method-post";
    case "PUT":
    case "PATCH":
      return "method-put";
    case "DELETE":
      return "method-delete";
    default:
      return "method-other";
  }
});

const statusClass = computed(() => {
  const s = props.session.responseStatus;
  if (s === 0) return "status-pending";
  if (s < 300) return "status-ok";
  if (s < 400) return "status-redirect";
  if (s < 500) return "status-client-err";
  return "status-server-err";
});

// ESC key closes the viewer
function onKeyDown(e: KeyboardEvent) {
  if (e.key === "Escape") emit("close");
}
onMounted(() => document.addEventListener("keydown", onKeyDown));
onBeforeUnmount(() => document.removeEventListener("keydown", onKeyDown));
</script>

<template>
  <div class="viewer-backdrop" @click.self="emit('close')">
    <div class="viewer-card">
      <!-- ── Header ─────────────────────────────────────────── -->
      <div class="viewer-header">
        <div class="viewer-url">
          <span class="method" :class="methodClass">{{ session.method }}</span>
          <span class="url" :title="session.url">{{ session.url }}</span>
        </div>
        <button class="close-btn" @click="emit('close')">✕</button>
      </div>

      <!-- ── Tabs ───────────────────────────────────────────── -->
      <div class="viewer-tabs">
        <button
          :class="['tab-btn', { active: tab === 'request' }]"
          @click="tab = 'request'"
        >
          Request
        </button>
        <button
          :class="['tab-btn', { active: tab === 'response' }]"
          @click="tab = 'response'"
        >
          <template v-if="session.responseStatus === 0">
            <span :class="statusClass">Pending…</span>
          </template>
          <template v-else>
            <span :class="statusClass">{{ session.responseStatus }}</span>
            &nbsp;Response · {{ session.durationMs }}ms
          </template>
        </button>
      </div>

      <!-- ── Tab content ────────────────────────────────────── -->
      <div class="viewer-content">
        <!-- REQUEST TAB -->
        <template v-if="tab === 'request'">
          <!-- Headers -->
          <div class="section">
            <div
              class="section-title collapsible"
              @click="reqHeadersOpen = !reqHeadersOpen"
            >
              <span class="chevron">{{ reqHeadersOpen ? "▼" : "▶" }}</span>
              Headers ({{ reqHeaders.length }})
            </div>
            <div v-if="reqHeadersOpen" class="headers-grid">
              <template v-for="[k, v] in reqHeaders" :key="k">
                <span class="hdr-key">{{ k }}</span>
                <span class="hdr-val">{{ v }}</span>
              </template>
            </div>
          </div>

          <!-- Body -->
          <div class="section body-section">
            <div class="section-toolbar">
              <span class="section-label">Body</span>
              <div class="toolbar-right">
                <button class="tool-btn" @click="reqRawMode = !reqRawMode">
                  {{ reqRawMode ? "Tree" : "Raw" }}
                </button>
                <template v-if="!reqRawMode && parsedReqBody !== null">
                  <button class="tool-btn" @click="reqExpandAll">
                    Expand All
                  </button>
                  <button class="tool-btn" @click="reqCollapseAll">
                    Collapse All
                  </button>
                </template>
                <button class="tool-btn copy-btn" @click="copyReqBody">
                  {{ reqCopied ? "✓ Copied" : "Copy" }}
                </button>
              </div>
            </div>
            <div class="body-container">
              <pre v-if="reqRawMode" class="body-raw">{{
                session.requestBody || "(empty)"
              }}</pre>
              <div v-else-if="parsedReqBody !== null" class="body-tree">
                <JsonTree
                  :key="reqTreeKey"
                  :data="parsedReqBody"
                  :depth="0"
                  :force-open="reqForceOpen"
                />
              </div>
              <pre v-else class="body-raw">{{
                session.requestBody || "(empty)"
              }}</pre>
            </div>
          </div>
        </template>

        <!-- RESPONSE TAB -->
        <template v-if="tab === 'response'">
          <!-- Headers -->
          <div class="section">
            <div
              class="section-title collapsible"
              @click="resHeadersOpen = !resHeadersOpen"
            >
              <span class="chevron">{{ resHeadersOpen ? "▼" : "▶" }}</span>
              Headers ({{ resHeaders.length }})
            </div>
            <div v-if="resHeadersOpen" class="headers-grid">
              <template v-for="[k, v] in resHeaders" :key="k">
                <span class="hdr-key">{{ k }}</span>
                <span class="hdr-val">{{ v }}</span>
              </template>
            </div>
          </div>

          <!-- Body -->
          <div class="section body-section">
            <div class="section-toolbar">
              <span class="section-label">Body</span>
              <div class="toolbar-right">
                <template v-if="!isResponseImage">
                  <button class="tool-btn" @click="resRawMode = !resRawMode">
                    {{ resRawMode ? "Tree" : "Raw" }}
                  </button>
                  <template v-if="!resRawMode && parsedResBody !== null">
                    <button class="tool-btn" @click="resExpandAll">
                      Expand All
                    </button>
                    <button class="tool-btn" @click="resCollapseAll">
                      Collapse All
                    </button>
                  </template>
                </template>
                <a v-if="isResponseImage && responseImageSrc" :href="responseImageSrc" download class="tool-btn">
                  Download
                </a>
                <button class="tool-btn copy-btn" @click="copyResBody">
                  {{ resCopied ? "✓ Copied" : "Copy" }}
                </button>
              </div>
            </div>
            <div class="body-container">
              <div v-if="isResponseImage && responseImageSrc && !resRawMode" class="body-image-container">
                <img :src="responseImageSrc" class="body-image" />
              </div>
              <pre v-else-if="resRawMode" class="body-raw">{{
                session.responseBody || "(empty)"
              }}</pre>
              <div v-else-if="parsedResBody !== null" class="body-tree">
                <JsonTree
                  :key="resTreeKey"
                  :data="parsedResBody"
                  :depth="0"
                  :force-open="resForceOpen"
                />
              </div>
              <pre v-else class="body-raw">{{
                session.responseBody || "(empty)"
              }}</pre>
            </div>
          </div>
        </template>
      </div>
    </div>
  </div>
</template>

<style scoped>
.viewer-backdrop {
  position: fixed;
  inset: 0;
  background: rgba(0, 0, 0, 0.72);
  z-index: 1000;
  display: flex;
  align-items: center;
  justify-content: center;
}

.viewer-card {
  background: #1e1e1e;
  border: 1px solid #3e3e42;
  border-radius: 6px;
  width: 90vw;
  height: 90vh;
  display: flex;
  flex-direction: column;
  overflow: hidden;
}

/* ── Header ───────────────────────────────────── */
.viewer-header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  padding: 10px 14px;
  border-bottom: 1px solid #3e3e42;
  flex-shrink: 0;
  gap: 12px;
  min-width: 0;
}

.viewer-url {
  display: flex;
  align-items: center;
  gap: 8px;
  min-width: 0;
  overflow: hidden;
}

.url {
  color: #d4d4d4;
  font-size: 12px;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.method {
  font-size: 11px;
  font-weight: bold;
  padding: 2px 6px;
  border-radius: 3px;
  flex-shrink: 0;
}
.method-get {
  background: #1e3a2f;
  color: #4ec9b0;
}
.method-post {
  background: #1e2a3f;
  color: #569cd6;
}
.method-put {
  background: #3a2e1e;
  color: #dcdcaa;
}
.method-delete {
  background: #3a1e1e;
  color: #f44747;
}
.method-other {
  background: #2d2d2d;
  color: #d4d4d4;
}

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
.close-btn:hover {
  color: #d4d4d4;
}

/* ── Tabs ─────────────────────────────────────── */
.viewer-tabs {
  display: flex;
  border-bottom: 1px solid #3e3e42;
  flex-shrink: 0;
}

.tab-btn {
  background: none;
  border: none;
  border-bottom: 2px solid transparent;
  color: #858585;
  cursor: pointer;
  padding: 8px 16px;
  font-size: 12px;
  font-family: inherit;
}
.tab-btn:hover {
  color: #d4d4d4;
  background: #252526;
}
.tab-btn.active {
  color: #d4d4d4;
  border-bottom-color: #007acc;
}

.status-pending {
  color: #858585;
}

.status-ok {
  color: #4ec9b0;
}
.status-redirect {
  color: #dcdcaa;
}
.status-client-err {
  color: #f44747;
}
.status-server-err {
  color: #f44747;
}

/* ── Content area ─────────────────────────────── */
.viewer-content {
  flex: 1;
  overflow-y: auto;
  display: flex;
  flex-direction: column;
  min-height: 0;
}

.section {
  border-bottom: 1px solid #2d2d2d;
}

.body-section {
  flex: 1;
  display: flex;
  flex-direction: column;
  min-height: 0;
}

.section-title {
  padding: 8px 14px;
  font-size: 11px;
  color: #858585;
  text-transform: uppercase;
  letter-spacing: 0.05em;
  display: flex;
  align-items: center;
  gap: 6px;
}
.section-title.collapsible {
  cursor: pointer;
  user-select: none;
}
.section-title.collapsible:hover {
  background: #252526;
}

.chevron {
  font-size: 9px;
}

.headers-grid {
  display: grid;
  grid-template-columns: minmax(140px, auto) 1fr;
  gap: 3px 12px;
  padding: 6px 14px 12px;
  font-size: 11px;
}
.hdr-key {
  color: #9cdcfe;
  word-break: break-all;
}
.hdr-val {
  color: #ce9178;
  word-break: break-all;
}

.section-toolbar {
  display: flex;
  align-items: center;
  justify-content: space-between;
  padding: 6px 14px;
  border-bottom: 1px solid #2d2d2d;
  flex-shrink: 0;
}
.section-label {
  font-size: 11px;
  color: #858585;
  text-transform: uppercase;
  letter-spacing: 0.05em;
}
.toolbar-right {
  display: flex;
  gap: 5px;
}

.tool-btn {
  background: #2d2d30;
  border: 1px solid #3e3e42;
  color: #d4d4d4;
  cursor: pointer;
  padding: 3px 8px;
  font-size: 11px;
  border-radius: 3px;
  font-family: inherit;
}
.tool-btn:hover {
  background: #3e3e42;
}
.copy-btn.copied,
.copy-btn:active {
  color: #4ec9b0;
}

.body-container {
  flex: 1;
  overflow: auto;
  padding: 12px 14px;
  min-height: 0;
}

.body-raw {
  font-family: "Cascadia Code", "Fira Code", "Consolas", monospace;
  font-size: 12px;
  white-space: pre-wrap;
  word-break: break-all;
  color: #d4d4d4;
  margin: 0;
  line-height: 1.5;
}

.body-tree {
  font-family: "Cascadia Code", "Fira Code", "Consolas", monospace;
  font-size: 12px;
  line-height: 1.5;
}

.body-image-container {
  display: flex;
  align-items: flex-start;
  justify-content: center;
  padding: 8px 0;
}

.body-image {
  max-width: 100%;
  max-height: 70vh;
  object-fit: contain;
  border-radius: 4px;
  border: 1px solid #3e3e42;
}
</style>
