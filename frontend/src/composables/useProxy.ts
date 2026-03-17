import { ref, readonly } from "vue";
import { HubConnectionBuilder, HubConnectionState } from "@microsoft/signalr";
import type { ProxySession, AutoResponderRule } from "../types";

const sessions = ref<ProxySession[]>([]);
const rules = ref<AutoResponderRule[]>([]);
const pendingSession = ref<ProxySession | null>(null);
const connected = ref(false);

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

function clearSessions() {
  sessions.value = [];
}

async function loadRules() {
  const r = await fetch("/api/auto-responder");
  rules.value = await r.json();
}

async function createRule(
  rule: Partial<AutoResponderRule>,
): Promise<AutoResponderRule> {
  const r = await fetch("/api/auto-responder", {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(rule),
  });
  const created = await r.json();
  rules.value.push(created);
  return created;
}

async function updateRule(rule: AutoResponderRule) {
  await fetch(`/api/auto-responder/${rule.id}`, {
    method: "PUT",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(rule),
  });
  const idx = rules.value.findIndex((r) => r.id === rule.id);
  if (idx >= 0) rules.value[idx] = rule;
}

async function deleteRule(id: string) {
  await fetch(`/api/auto-responder/${id}`, { method: "DELETE" });
  rules.value = rules.value.filter((r) => r.id !== id);
}

export function useProxy() {
  return {
    sessions: readonly(sessions),
    rules,
    pendingSession,
    connected: readonly(connected),
    connect,
    loadSessions,
    clearSessions,
    loadRules,
    createRule,
    updateRule,
    deleteRule,
  };
}
