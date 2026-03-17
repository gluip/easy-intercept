<script setup lang="ts">
import { ref, onMounted } from 'vue'
import type { ProxySession } from './types'
import { useProxy } from './composables/useProxy'
import SessionList from './components/SessionList.vue'
import SessionDetail from './components/SessionDetail.vue'

const { sessions, connected, connect, loadSessions, loadPins, pinSession, unpinUrl, clearSessions, isPinned } = useProxy()

const selected = ref<ProxySession | null>(null)

function selectSession(s: ProxySession) {
  selected.value = s
}

async function handlePin(id: string) {
  await pinSession(id)
}

async function handleUnpin(url: string) {
  await unpinUrl(url)
}

function handleClear() {
  clearSessions()
  selected.value = null
}

onMounted(async () => {
  await connect()
  await loadSessions()
  await loadPins()
})
</script>

<template>
  <div class="app">
    <header>
      <h1>EasyIntercept</h1>
      <small>proxy → localhost:8888 &nbsp;|&nbsp; ui → localhost:8080</small>
    </header>

    <div class="toolbar">
      <button @click="handleClear">Clear</button>
      <button @click="loadSessions">Reload</button>
    </div>

    <div class="main">
      <SessionList
        :sessions="sessions"
        :selected-id="selected?.id ?? null"
        @select="selectSession"
      />

      <SessionDetail
        v-if="selected"
        :session="selected"
        :pinned="isPinned(selected.url)"
        @pin="handlePin"
        @unpin="handleUnpin"
      />
      <div v-else class="detail-placeholder">
        Select a request to inspect
      </div>
    </div>

    <div class="status-bar">
      <span :class="{ disconnected: !connected }">
        {{ connected ? '● Connected' : '✕ Disconnected' }}
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
  font-family: 'Cascadia Code', 'Fira Code', 'Consolas', monospace;
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
  gap: 8px;
  flex-shrink: 0;
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
