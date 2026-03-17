<script setup lang="ts">
import { ref, computed, onMounted } from "vue";
import type { ProxySession } from "./types";
import { useProxy } from "./composables/useProxy";
import SessionList from "./components/SessionList.vue";
import SessionDetail from "./components/SessionDetail.vue";
import AutoResponder from "./components/AutoResponder.vue";
import Recordings from "./components/Recordings.vue";

const {
  sessions,
  connected,
  recordingStatus,
  pendingSession,
  connect,
  loadSessions,
  clearSessions,
  replaySession,
  deleteSessions,
  loadRecordingStatus,
} = useProxy();

const selectedIds = ref<string[]>([]);
const tab = ref<"requests" | "autoresponder" | "recordings">("requests");

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

function handleAddAutoResponse(session: ProxySession) {
  pendingSession.value = session;
  tab.value = "autoresponder";
}

function handleCopyUrl(session: ProxySession) {
  navigator.clipboard.writeText(session.url);
}

async function handleReplay(session: ProxySession) {
  await replaySession(session.id);
  await loadSessions();
}

async function handleDeleteSelected(ids: string[]) {
  await deleteSessions(ids);
  selectedIds.value = selectedIds.value.filter((id) => !ids.includes(id));
}

onMounted(async () => {
  try {
    await connect();
  } catch (e) {
    console.error("SignalR connect failed:", e);
  }
  await loadSessions();
  await loadRecordingStatus();
});
</script>

<template>
  <div class="app">
    <header>
      <h1>EasyIntercept</h1>
      <small>proxy → localhost:8888 &nbsp;|&nbsp; ui → localhost:8080</small>
    </header>

    <div class="toolbar">
      <div class="tabs">
        <button
          :class="{ active: tab === 'requests' }"
          @click="tab = 'requests'"
        >
          Requests
        </button>
        <button
          :class="{ active: tab === 'autoresponder' }"
          @click="tab = 'autoresponder'"
        >
          Auto Responder
        </button>
        <button
          :class="{ active: tab === 'recordings' }"
          @click="tab = 'recordings'"
        >
          Recordings
        </button>
      </div>
      <div class="toolbar-status">
        <span v-if="recordingStatus.recordingId" class="status-recording"
          >⏺ Recording</span
        >
        <span v-if="recordingStatus.activeId" class="status-playback"
          >▶ Playback</span
        >
      </div>
      <div class="toolbar-actions" v-if="tab === 'requests'">
        <button @click="handleClear">Clear</button>
        <button @click="loadSessions">Reload</button>
      </div>
    </div>

    <div class="main" v-if="tab === 'requests'">
      <SessionList
        :sessions="sessions"
        :selected-ids="selectedIds"
        @select="selectSessions"
        @copy-url="handleCopyUrl"
        @replay="handleReplay"
        @add-auto-response="handleAddAutoResponse"
        @delete-selected="handleDeleteSelected"
      />

      <SessionDetail
        v-if="selectedSession"
        :session="selectedSession"
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

    <div class="main" v-else-if="tab === 'autoresponder'">
      <AutoResponder />
    </div>

    <div class="main" v-else>
      <Recordings />
    </div>

    <div class="status-bar">
      <span :class="{ disconnected: !connected }">
        {{ connected ? "● Connected" : "✕ Disconnected" }}
      </span>
      <span>{{ sessions.length }} requests</span>
    </div>
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
  justify-content: space-between;
  align-items: center;
  gap: 8px;
  flex-shrink: 0;
}
.tabs {
  display: flex;
  gap: 0;
}
.tabs button {
  background: transparent;
  border: none;
  border-bottom: 2px solid transparent;
  color: #858585;
  padding: 4px 14px;
  font-size: 12px;
  cursor: pointer;
}
.tabs button:hover {
  color: #d4d4d4;
}
.tabs button.active {
  color: #d4d4d4;
  border-bottom-color: #007acc;
}
.toolbar-actions {
  display: flex;
  gap: 8px;
}
.toolbar-status {
  display: flex;
  gap: 12px;
  align-items: center;
  margin-left: auto;
}
.status-recording {
  color: #f48771;
  font-size: 11px;
  animation: pulse 1.5s ease-in-out infinite;
}
.status-playback {
  color: #4ec9b0;
  font-size: 11px;
}
@keyframes pulse {
  0%,
  100% {
    opacity: 1;
  }
  50% {
    opacity: 0.4;
  }
}

.main {
  display: flex;
  flex: 1;
  overflow: hidden;
}

.detail-placeholder {
  width: 50%;
  color: #555;
  padding: 40px;
  text-align: center;
}

.status-bar {
  background: #007acc;
  color: white;
  padding: 3px 12px;
  font-size: 11px;
  display: flex;
  gap: 20px;
  flex-shrink: 0;
}
.disconnected {
  color: #ffcccc;
}
</style>
