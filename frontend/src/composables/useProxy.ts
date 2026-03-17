import { ref, readonly } from 'vue'
import { HubConnectionBuilder, HubConnectionState } from '@microsoft/signalr'
import type { ProxySession } from './types'

const sessions = ref<ProxySession[]>([])
const pinnedUrls = ref<Set<string>>(new Set())
const connected = ref(false)

const connection = new HubConnectionBuilder()
  .withUrl('/proxy-hub')
  .withAutomaticReconnect()
  .build()

connection.on('NewSession', (s: ProxySession) => {
  sessions.value.unshift(s)
})

connection.onreconnected(() => (connected.value = true))
connection.onclose(() => (connected.value = false))

async function connect() {
  if (connection.state === HubConnectionState.Disconnected) {
    await connection.start()
    connected.value = true
  }
}

async function loadSessions() {
  const r = await fetch('/api/sessions')
  sessions.value = await r.json()
}

async function loadPins() {
  const r = await fetch('/api/pins')
  const data: Record<string, unknown> = await r.json()
  pinnedUrls.value = new Set(Object.keys(data))
}

async function pinSession(id: string) {
  await fetch(`/api/sessions/${id}/pin`, { method: 'POST' })
  await loadPins()
}

async function unpinUrl(url: string) {
  await fetch(`/api/pins?url=${encodeURIComponent(url)}`, { method: 'DELETE' })
  await loadPins()
}

function clearSessions() {
  sessions.value = []
}

function isPinned(url: string) {
  return pinnedUrls.value.has(url)
}

export function useProxy() {
  return {
    sessions: readonly(sessions),
    pinnedUrls: readonly(pinnedUrls),
    connected: readonly(connected),
    connect,
    loadSessions,
    loadPins,
    pinSession,
    unpinUrl,
    clearSessions,
    isPinned,
  }
}
