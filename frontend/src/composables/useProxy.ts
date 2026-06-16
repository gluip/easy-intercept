import { ref, readonly } from "vue";
import { HubConnectionBuilder, HubConnectionState } from "@microsoft/signalr";
import type { ProxySession, AutoResponderRule } from "../types";

const sessions = ref<ProxySession[]>([]);
const connected = ref(false);
const pendingSession = ref<ProxySession | null>(null);

const autoResponderRules = ref<AutoResponderRule[]>([]);
const pendingRule = ref<AutoResponderRule | null>(null);

const systemProxyEnabled = ref(false);

const connection = new HubConnectionBuilder()
  .withUrl("/proxy-hub")
  .withAutomaticReconnect()
  .build();

connection.on("NewSession", (s: ProxySession) => {
  sessions.value.unshift(s);
});

connection.on("UpdateSession", (s: ProxySession) => {
  const idx = sessions.value.findIndex((x) => x.id === s.id);
  if (idx >= 0) sessions.value[idx] = s;
  else sessions.value.unshift(s);
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

async function loadRules() {
  const r = await fetch("/api/auto-responders");
  autoResponderRules.value = await r.json();
}

async function addRule(rule: AutoResponderRule) {
  await fetch("/api/auto-responders", {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(rule),
  });
  autoResponderRules.value.push(rule);
}

async function updateRule(rule: AutoResponderRule) {
  await fetch(`/api/auto-responders/${rule.id}`, {
    method: "PUT",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(rule),
  });
  const idx = autoResponderRules.value.findIndex((r) => r.id === rule.id);
  if (idx >= 0) autoResponderRules.value[idx] = rule;
}

async function deleteRule(id: string) {
  await fetch(`/api/auto-responders/${id}`, { method: "DELETE" });
  autoResponderRules.value = autoResponderRules.value.filter((r) => r.id !== id);
}

async function toggleRule(id: string) {
  const rule = autoResponderRules.value.find((r) => r.id === id);
  if (!rule) return;
  await updateRule({ ...rule, isEnabled: !rule.isEnabled });
}

async function loadSystemProxy() {
  const r = await fetch("/api/system-proxy");
  const data = await r.json();
  systemProxyEnabled.value = data.enabled;
}

async function setSystemProxy(enabled: boolean) {
  const r = await fetch("/api/system-proxy", {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ enabled }),
  });
  const data = await r.json();
  systemProxyEnabled.value = data.enabled;
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
    autoResponderRules: readonly(autoResponderRules),
    pendingRule,
    loadRules,
    addRule,
    updateRule,
    deleteRule,
    toggleRule,
    systemProxyEnabled: readonly(systemProxyEnabled),
    loadSystemProxy,
    setSystemProxy,
  };
}
