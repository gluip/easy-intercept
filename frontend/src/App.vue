<script setup lang="ts">
import { ref, computed, onMounted } from "vue";
import type { ProxySession, AutoResponderRule } from "./types";
import { useProxy } from "./composables/useProxy";
import { detectLLMProvider } from "./utils/llm-detection";
import { calcCost, formatCost } from "./utils/llm-cost";
import { isStreamingResponse, parseOpenAIStream, parseAnthropicStream, parseCopilotResponsesStream, isOpenAIResponsesRequest, parseOpenAIResponses } from "./utils/llm-stream-parser";
import SessionList from "./components/SessionList.vue";
import SessionDetail from "./components/SessionDetail.vue";
import SessionViewer from "./components/SessionViewer.vue";
import AutoResponder from "./components/AutoResponder.vue";
import CompareViewer from "./components/CompareViewer.vue";

const {
  sessions,
  connected,
  connect,
  loadSessions,
  clearSessions,
  replaySession,
  deleteSessions,
  autoResponderRules,
  pendingRule,
  loadRules,
  addRule,
  updateRule,
  deleteRule,
  toggleRule,
  systemProxyEnabled,
  loadSystemProxy,
  setSystemProxy,
  availableBrowsers,
  loadBrowsers,
  launchBrowser,
} = useProxy();

const systemProxyBusy = ref(false);

async function toggleSystemProxy() {
  systemProxyBusy.value = true;
  try {
    await setSystemProxy(!systemProxyEnabled.value);
  } catch (e) {
    console.error("Failed to toggle system proxy:", e);
  } finally {
    systemProxyBusy.value = false;
  }
}

const browserLaunchBusy = ref(false);

async function launchBrowserById(id: string) {
  browserLaunchBusy.value = true;
  try {
    await launchBrowser(id);
  } catch (e) {
    console.error("Failed to launch browser:", e);
  } finally {
    browserLaunchBusy.value = false;
  }
}

const selectedIds = ref<string[]>([]);
const listWidth = ref(600);
const activeTab = ref<"requests" | "auto-responder">("requests");

const selectedSession = computed(() =>
  selectedIds.value.length === 1
    ? (sessions.value.find((s) => s.id === selectedIds.value[0]) ?? null)
    : null,
);
function formatMs(ms: number): string {
  if (ms >= 1000) return `${(ms / 1000).toFixed(2)}s`;
  return `${Math.round(ms)}ms`;
}

const selectionStats = computed(() => {
  if (selectedIds.value.length <= 1) return null;
  const selected = sessions.value.filter((s) => selectedIds.value.includes(s.id));
  let promptTokens = 0, responseTokens = 0, cachedTokens = 0, thoughtTokens = 0;
  let totalCost = 0;
  let llmCount = 0;

  let totalWaitTime = 0;
  let minStart = Infinity, maxEnd = -Infinity;
  for (const s of selected) {
    const start = new Date(s.timestamp).getTime();
    const dur = Math.max(s.responseStatus === 0 ? Date.now() - start : s.durationMs, 0);
    totalWaitTime += dur;
    minStart = Math.min(minStart, start);
    maxEnd = Math.max(maxEnd, start + dur);
  }
  const wallClockTime = selected.length > 0 ? Math.max(maxEnd - minStart, 0) : 0;

  for (const s of selected) {
    const provider = detectLLMProvider(s);
    if (!provider || s.responseStatus === 0) continue;
    try {
      let p = 0, r = 0, c = 0, t = 0, model = "unknown";

      if (provider === "copilot") {
        const parsed = parseCopilotResponsesStream(s.responseBody);
        p = parsed.promptTokens; r = parsed.responseTokens; c = parsed.cachedTokens;
        model = parsed.model;
      } else if (provider === "openai" && isOpenAIResponsesRequest(s.requestBody)) {
        const parsed = parseOpenAIResponses(s.responseBody);
        p = parsed.promptTokens; r = parsed.responseTokens;
        c = parsed.cachedTokens; t = parsed.thoughtTokens;
        model = parsed.model;
      } else {
        let res: any;
        if (isStreamingResponse(s.responseBody)) {
          if (provider === "openai") res = parseOpenAIStream(s.responseBody);
          else if (provider === "anthropic") res = parseAnthropicStream(s.responseBody);
          else res = JSON.parse(s.responseBody);
        } else {
          res = JSON.parse(s.responseBody);
        }

        if (provider === "gemini") {
          const u = res.usageMetadata ?? {};
          p = u.promptTokenCount ?? 0; r = u.candidatesTokenCount ?? 0;
          c = u.cachedContentTokenCount ?? 0; t = u.thoughtsTokenCount ?? 0;
          model = res.modelVersion ?? "unknown";
        } else if (provider === "anthropic") {
          const u = res.usage ?? {};
          p = u.input_tokens ?? 0; r = u.output_tokens ?? 0;
          c = u.cache_read_input_tokens ?? 0;
          const req = JSON.parse(s.requestBody);
          model = res.model ?? req.model ?? "unknown";
        } else if (provider === "openai") {
          const u = res.usage ?? {};
          p = u.prompt_tokens ?? 0; r = u.completion_tokens ?? 0;
          c = (u.prompt_tokens_details ?? {}).cached_tokens ?? 0;
          const req = JSON.parse(s.requestBody);
          model = res.model ?? req.model ?? "unknown";
        }
      }

      promptTokens  += p; responseTokens += r;
      cachedTokens  += c; thoughtTokens  += t;
      const cost = calcCost(provider, model, p, r, c, t);
      if (cost) totalCost += cost.total;
      llmCount++;
    } catch { /* skip */ }
  }

  return { count: selected.length, llmCount, promptTokens, responseTokens, cachedTokens, thoughtTokens, totalCost, totalWaitTime, wallClockTime };
});

function selectSessions(ids: string[]) {
  selectedIds.value = ids;
}

function handleClear() {
  clearSessions();
  selectedIds.value = [];
}

async function handleReplay(session: ProxySession) {
  await replaySession(session.id);
  await loadSessions();
}

async function handleDeleteSelected(ids: string[]) {
  await deleteSessions(ids);
  selectedIds.value = selectedIds.value.filter((id) => !ids.includes(id));
}

// Session viewer
const viewerSession = ref<ProxySession | null>(null);
const viewerTab = ref<"request" | "response">("request");

// Compare viewer
const comparePair = ref<[ProxySession, ProxySession] | null>(null);

function handleCompare(ids: [string, string]) {
  const a = sessions.value.find((s) => s.id === ids[0]);
  const b = sessions.value.find((s) => s.id === ids[1]);
  if (a && b) comparePair.value = [a, b];
}

function handleCopyUrl(session: ProxySession) {
  navigator.clipboard.writeText(session.url);
}

function openViewer(session: ProxySession, tab: "request" | "response") {
  viewerSession.value = session;
  viewerTab.value = tab;
}

function closeViewer() {
  viewerSession.value = null;
}

function startDrag(e: MouseEvent) {
  const startX = e.clientX;
  const startWidth = listWidth.value;
  document.body.style.userSelect = "none";
  document.body.style.cursor = "col-resize";

  function onMove(ev: MouseEvent) {
    listWidth.value = Math.max(200, Math.min(window.innerWidth - 200, startWidth + ev.clientX - startX));
  }
  function onUp() {
    document.body.style.userSelect = "";
    document.body.style.cursor = "";
    document.removeEventListener("mousemove", onMove);
    document.removeEventListener("mouseup", onUp);
  }
  document.addEventListener("mousemove", onMove);
  document.addEventListener("mouseup", onUp);
}

const HOP_BY_HOP = new Set([
  "connection", "keep-alive", "transfer-encoding", "te", "trailers",
  "upgrade", "proxy-authenticate", "proxy-authorization", "proxy-connection",
  "content-encoding",
]);

function handleAddAutoResponse(session: ProxySession) {
  const cleanHeaders = Object.fromEntries(
    Object.entries(session.responseHeaders).filter(
      ([k]) => !HOP_BY_HOP.has(k.toLowerCase()),
    ),
  );
  pendingRule.value = {
    id: crypto.randomUUID(),
    name: `${session.method} ${new URL(session.url).pathname}`,
    isEnabled: true,
    method: session.method,
    url: session.url,
    responseStatus: session.responseStatus,
    responseHeaders: cleanHeaders,
    responseBody: session.responseBody,
    latencyMs: 0,
    bodyMatchType: "none",
    bodyMatch: "",
  } as AutoResponderRule;
  activeTab.value = "auto-responder";
}

onMounted(async () => {
  listWidth.value = Math.round(window.innerWidth * 0.5);
  try {
    await connect();
  } catch (e) {
    console.error("SignalR connect failed:", e);
  }
  await loadSessions();
  await loadRules();
  try {
    await loadSystemProxy();
  } catch (e) {
    console.error("Failed to load system proxy state:", e);
  }
  try {
    await loadBrowsers();
  } catch (e) {
    console.error("Failed to load browser-launch state:", e);
  }
});
</script>

<template>
  <div class="app">
    <header>
      <h1>EasyIntercept</h1>
      <small>proxy → localhost:9999 &nbsp;|&nbsp; ui → localhost:8080</small>
      <button
        class="system-proxy-btn"
        :class="{ active: systemProxyEnabled }"
        :disabled="systemProxyBusy"
        @click="toggleSystemProxy"
        :title="systemProxyEnabled ? 'Klik om systeem-proxy uit te zetten' : 'Klik om Windows als systeem-proxy te gebruiken (127.0.0.1:9999)'"
      >
        <span class="dot" />
        {{ systemProxyEnabled ? "System proxy: ON" : "System proxy: OFF" }}
      </button>
      <div v-if="availableBrowsers.length" class="browser-launch-group">
        <button
          v-for="b in availableBrowsers"
          :key="b.id"
          class="browser-launch-btn"
          :disabled="browserLaunchBusy"
          @click="launchBrowserById(b.id)"
          title="Start een geïsoleerd browservenster dat alleen zelf door de proxy gaat (vereist dat de EasyIntercept CA al is geïnstalleerd; het venster start uitgelogd in een vers profiel)"
        >
          <svg class="chrome-icon" viewBox="0 0 24 24" width="14" height="14" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">
            <circle cx="12" cy="12" r="10" />
            <circle cx="12" cy="12" r="4" />
            <line x1="21.17" y1="8" x2="12" y2="8" />
            <line x1="3.95" y1="6.06" x2="8.54" y2="14" />
            <line x1="10.88" y1="21.94" x2="15.46" y2="14" />
          </svg>
          Launch proxied {{ b.name }}
        </button>
      </div>
    </header>

    <div class="tab-bar">
      <button
        :class="['tab', { active: activeTab === 'requests' }]"
        @click="activeTab = 'requests'"
      >
        Requests
      </button>
      <button
        :class="['tab', { active: activeTab === 'auto-responder' }]"
        @click="activeTab = 'auto-responder'"
      >
        ⚡ Auto Responder
        <span v-if="autoResponderRules.length > 0" class="rule-count">
          {{ autoResponderRules.filter((r) => r.isEnabled).length }}/{{ autoResponderRules.length }}
        </span>
      </button>
    </div>

    <div class="toolbar" v-show="activeTab === 'requests'">
      <div class="toolbar-actions">
        <button @click="handleClear">Clear</button>
        <button @click="loadSessions">Reload</button>
      </div>
      <span class="session-count">{{ sessions.length }} requests</span>
    </div>

    <div class="main" v-show="activeTab === 'requests'">
      <div class="list-pane" :style="{ width: listWidth + 'px' }">
        <SessionList
          :sessions="sessions"
          :selected-ids="selectedIds"
          @select="selectSessions"
          @replay="handleReplay"
          @delete-selected="handleDeleteSelected"
          @compare="handleCompare"
          @copy-url="handleCopyUrl"
        />
      </div>
      <div class="divider" @mousedown.prevent="startDrag" />

      <SessionDetail
        v-if="selectedSession"
        :session="selectedSession"
        @open-viewer="openViewer"
        @add-auto-response="handleAddAutoResponse"
      />
      <div v-else class="detail-placeholder">
        <template v-if="selectionStats">
          <div class="sel-count">{{ selectionStats.count }} requests selected</div>
          <div class="sel-timing">
            <span title="Sum of each request's own duration">⏳ wait {{ formatMs(selectionStats.totalWaitTime) }}</span>
            <span title="Wall-clock time from first start to last finish">🕒 wall {{ formatMs(selectionStats.wallClockTime) }}</span>
          </div>
          <template v-if="selectionStats.llmCount > 0">
            <div class="sel-tokens">
              <span title="Input tokens">↑ {{ selectionStats.promptTokens.toLocaleString() }}</span>
              <span v-if="selectionStats.cachedTokens" title="Cached tokens">💾 {{ selectionStats.cachedTokens.toLocaleString() }}</span>
              <span v-if="selectionStats.thoughtTokens" title="Thinking tokens">💭 {{ selectionStats.thoughtTokens.toLocaleString() }}</span>
              <span title="Output tokens">↓ {{ selectionStats.responseTokens.toLocaleString() }}</span>
            </div>
            <div v-if="selectionStats.totalCost > 0" class="sel-cost">
              {{ formatCost({ total: selectionStats.totalCost, inputCost: 0, outputCost: 0, cachedCost: 0 }) }}
            </div>
          </template>
        </template>
        <template v-else>Select a request to inspect</template>
      </div>
    </div>

    <AutoResponder
      v-if="activeTab === 'auto-responder'"
      :rules="autoResponderRules"
      :pending-rule="pendingRule"
      @add="addRule"
      @update="updateRule"
      @delete="deleteRule"
      @toggle="toggleRule"
      @pending-consumed="pendingRule = null"
    />

    <div class="status-bar">
      <span :class="{ disconnected: !connected }">
        {{ connected ? "● Connected" : "✕ Disconnected" }}
      </span>
    </div>

    <SessionViewer
      v-if="viewerSession"
      :session="viewerSession"
      :initial-tab="viewerTab"
      @close="closeViewer"
    />

    <CompareViewer
      v-if="comparePair"
      :a="comparePair[0]"
      :b="comparePair[1]"
      @close="comparePair = null"
    />
  </div>
</template>

<style>
*,
*::before,
*::after {
  box-sizing: border-box;
  margin: 0;
  padding: 0;
}

body {
  font-family: "Cascadia Code", "Fira Code", "Consolas", monospace;
  background: #1e1e1e;
  color: #d4d4d4;
  overflow: hidden;
}

button {
  background: #3e3e42;
  color: #d4d4d4;
  border: 1px solid #555;
  padding: 4px 10px;
  cursor: pointer;
  font-family: inherit;
  font-size: 12px;
}
button:hover {
  background: #4e4e52;
}
</style>

<style scoped>
.app {
  display: flex;
  flex-direction: column;
  height: 100vh;
  overflow: hidden;
}

header {
  background: #252526;
  border-bottom: 1px solid #3e3e42;
  padding: 10px 16px;
  display: flex;
  align-items: center;
  gap: 16px;
  flex-shrink: 0;
}
header h1 {
  font-size: 14px;
  color: #4ec9b0;
}
header small {
  color: #858585;
  font-size: 12px;
}

.system-proxy-btn {
  margin-left: auto;
  display: flex;
  align-items: center;
  gap: 8px;
  background: #3c3c3c;
  border: 1px solid #555;
  color: #858585;
  padding: 5px 12px;
  border-radius: 3px;
  font-size: 11px;
  letter-spacing: 0.03em;
}
.system-proxy-btn:hover:not(:disabled) {
  color: #d4d4d4;
  border-color: #569cd6;
}
.system-proxy-btn:disabled {
  opacity: 0.6;
  cursor: wait;
}
.system-proxy-btn .dot {
  width: 8px;
  height: 8px;
  border-radius: 50%;
  background: #555;
  flex-shrink: 0;
}
.system-proxy-btn.active {
  color: #4ec9b0;
  border-color: #4ec9b0;
  background: #1e3a2f;
}
.system-proxy-btn.active .dot {
  background: #4ec9b0;
  box-shadow: 0 0 6px #4ec9b0;
}

.browser-launch-group {
  display: flex;
  align-items: center;
  gap: 6px;
}
.browser-launch-btn {
  display: flex;
  align-items: center;
  gap: 8px;
  background: #3c3c3c;
  border: 1px solid #555;
  color: #858585;
  padding: 5px 12px;
  border-radius: 3px;
  font-size: 11px;
  letter-spacing: 0.03em;
}
.browser-launch-btn:hover:not(:disabled) {
  color: #d4d4d4;
  border-color: #569cd6;
}
.browser-launch-btn:disabled {
  opacity: 0.6;
  cursor: wait;
}
.browser-launch-btn .chrome-icon {
  flex-shrink: 0;
}

.toolbar {
  background: #2d2d30;
  border-bottom: 1px solid #3e3e42;
  padding: 6px 16px;
  display: flex;
  align-items: center;
  gap: 8px;
  flex-shrink: 0;
}
.toolbar-actions {
  display: flex;
  gap: 8px;
}
.session-count {
  margin-left: auto;
  color: #858585;
  font-size: 12px;
}

.main {
  display: flex;
  flex: 1;
  overflow: hidden;
}

.list-pane {
  flex-shrink: 0;
  overflow: hidden;
  display: flex;
}

.divider {
  width: 4px;
  flex-shrink: 0;
  background: #3e3e42;
  cursor: col-resize;
  transition: background 0.15s;
}
.divider:hover {
  background: #569cd6;
}

.detail-placeholder {
  flex: 1;
  color: #555;
  padding: 40px;
  text-align: center;
  display: flex;
  flex-direction: column;
  align-items: center;
  justify-content: center;
  gap: 10px;
}

.sel-count {
  color: #858585;
  font-size: 13px;
}

.sel-timing {
  display: flex;
  gap: 12px;
  color: #dcdcaa;
  font-size: 13px;
  font-family: "Cascadia Code", "Fira Code", monospace;
}

.sel-tokens {
  display: flex;
  gap: 12px;
  color: #9cdcfe;
  font-size: 13px;
  font-family: "Cascadia Code", "Fira Code", monospace;
}

.sel-cost {
  color: #4ec9b0;
  font-size: 20px;
  font-weight: 600;
  font-family: "Cascadia Code", "Fira Code", monospace;
}

.tab-bar {
  background: #252526;
  border-bottom: 1px solid #3e3e42;
  display: flex;
  gap: 0;
  flex-shrink: 0;
  padding: 0 8px;
}

.tab {
  background: none;
  border: none;
  border-bottom: 2px solid transparent;
  color: #858585;
  cursor: pointer;
  padding: 8px 16px;
  font-size: 12px;
  font-family: inherit;
  display: flex;
  align-items: center;
  gap: 6px;
}
.tab:hover {
  color: #d4d4d4;
  background: #2d2d30;
}
.tab.active {
  color: #d4d4d4;
  border-bottom-color: #007acc;
}

.rule-count {
  font-size: 10px;
  color: #4ec9b0;
  background: #1e3a2f;
  padding: 1px 5px;
  border-radius: 8px;
}

.status-bar {
  background: #007acc;
  color: white;
  padding: 2px 16px;
  font-size: 11px;
  display: flex;
  gap: 16px;
  flex-shrink: 0;
}
.status-bar .disconnected {
  color: #ffcc00;
}
</style>
