<script setup lang="ts">
import { ref, computed, onMounted } from "vue";
import type { ProxySession } from "./types";
import { useProxy } from "./composables/useProxy";
import SessionList from "./components/SessionList.vue";
import SessionDetail from "./components/SessionDetail.vue";

const {
  sessions,
  connected,
  pendingSession,
  connect,
  loadSessions,
  clearSessions,
  replaySession,
  deleteSessions,
} = useProxy();

const selectedIds = ref<string[]>([]);

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

onMounted(async () => {
  try {
    await connect();
  } catch (e) {
    console.error("SignalR connect failed:", e);
  }
  await loadSessions();
});
</script>

<template>
  <div class="app">
    <header>
      <h1>EasyIntercept</h1>
      <small>proxy → localhost:8888 &nbsp;|&nbsp; ui → localhost:8080</small>
    </header>

    <div class="toolbar">
      <div class="toolbar-actions">
        <button @click="handleClear">Clear</button>
        <button @click="loadSessions">Reload</button>
      </div>
      <span class="session-count">{{ sessions.length }} requests</span>
    </div>

    <div class="main">
      <SessionList
        :sessions="sessions"
        :selected-ids="selectedIds"
        @select="selectSessions"
        @replay="handleReplay"
        @delete-selected="handleDeleteSelected"
      />

      <SessionDetail
        v-if="selectedSession"
        :session="selectedSession"
      />
      <div v-else class="detail-placeholder">
        {{
          selectedIds.length > 1
            ? `${selectedIds.length} requests selected`
            : "Select a request to inspect"
        }}
      </div>
    </div>

    <div class="status-bar">
      <span :class="{ disconnected: !connected }">
        {{ connected ? "● Connected" : "✕ Disconnected" }}
      </span>
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

.detail-placeholder {
  flex: 1;
  color: #555;
  padding: 40px;
  text-align: center;
  display: flex;
  align-items: center;
  justify-content: center;
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
