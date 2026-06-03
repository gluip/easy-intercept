<script setup lang="ts">
import { ref, computed, onMounted } from "vue";
import type { ProxySession, AutoResponderRule } from "./types";
import { useProxy } from "./composables/useProxy";
import SessionList from "./components/SessionList.vue";
import SessionDetail from "./components/SessionDetail.vue";
import SessionViewer from "./components/SessionViewer.vue";
import AutoResponder from "./components/AutoResponder.vue";

const {
  sessions,
  connected,
  pendingSession,
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
} = useProxy();

const selectedIds = ref<string[]>([]);
const listWidth = ref(600);
const activeTab = ref<"requests" | "auto-responder">("requests");

const selectedSession = computed(() =>
  selectedIds.value.length === 1
    ? (sessions.value.find((s) => s.id === selectedIds.value[0]) ?? null)
    : null,
);

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

function handleAddAutoResponse(session: ProxySession) {
  pendingRule.value = {
    id: crypto.randomUUID(),
    name: `${session.method} ${new URL(session.url).pathname}`,
    isEnabled: true,
    method: session.method,
    url: session.url,
    responseStatus: session.responseStatus,
    responseHeaders: { ...session.responseHeaders },
    responseBody: session.responseBody,
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
});
</script>

<template>
  <div class="app">
    <header>
      <h1>EasyIntercept</h1>
      <small>proxy → localhost:9999 &nbsp;|&nbsp; ui → localhost:8080</small>
    </header>

    <div class="toolbar" v-show="activeTab === 'requests'">
      <div class="toolbar-actions">
        <button @click="handleClear">Clear</button>
        <button @click="loadSessions">Reload</button>
      </div>
      <span class="session-count">{{ sessions.length }} requests</span>
    </div>

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

    <div class="main" v-show="activeTab === 'requests'">
      <div class="list-pane" :style="{ width: listWidth + 'px' }">
        <SessionList
          :sessions="sessions"
          :selected-ids="selectedIds"
          @select="selectSessions"
          @replay="handleReplay"
          @delete-selected="handleDeleteSelected"
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
        {{
          selectedIds.length > 1
            ? `${selectedIds.length} requests selected`
            : "Select a request to inspect"
        }}
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
  align-items: center;
  justify-content: center;
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
