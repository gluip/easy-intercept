import { ref, readonly } from "vue";
import { HubConnectionBuilder, HubConnectionState } from "@microsoft/signalr";
import type { ProxySession } from "../types";

const sessions = ref<ProxySession[]>([]);
const connected = ref(false);
const pendingSession = ref<ProxySession | null>(null);

const connection = new HubConnectionBuilder()
  .withUrl("/proxy-hub")
  .withAutomaticReconnect()
  .build();

connection.on("NewSession", (s: ProxySession) => {
  sessions.value.unshift(s);
});

connection.onreconnected(() => (connected.value = true));
connection.onclose(() => (connected.value = false));

async function connect() {
  if (connection.state === HubConnectionState.Disconnected) {
    await connection.start();
    connected.value = true;
  }
}

async function loadSessions() {
  const r = await fetch("/api/sessions");
  sessions.value = await r.json();
}

async function clearSessions() {
  await fetch("/api/sessions", { method: "DELETE" });
  sessions.value = [];
}

async function replaySession(id: string) {
  const r = await fetch(`/api/sessions/${id}/replay`, { method: "POST" });
  return r.json();
}

async function deleteSessions(ids: string[]) {
  await fetch("/api/sessions/delete", {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(ids),
  });
  sessions.value = sessions.value.filter((s) => !ids.includes(s.id));
}

export function useProxy() {
  return {
    sessions: readonly(sessions),
    connected: readonly(connected),
    pendingSession,
    connect,
    loadSessions,
    clearSessions,
    replaySession,
    deleteSessions,
  };
}
