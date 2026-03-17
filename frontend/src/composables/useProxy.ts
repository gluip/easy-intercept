import { ref, readonly } from "vue";
import { HubConnectionBuilder, HubConnectionState } from "@microsoft/signalr";
import type {
  ProxySession,
  AutoResponderRule,
  Recording,
  RecordingStatus,
} from "../types";

const sessions = ref<ProxySession[]>([]);
const rules = ref<AutoResponderRule[]>([]);
const recordings = ref<Recording[]>([]);
const recordingStatus = ref<RecordingStatus>({
  recordingId: null,
  activeId: null,
});
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

async function clearSessions() {
  await fetch("/api/sessions", { method: "DELETE" });
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

// --- Recordings ---

async function loadRecordings() {
  const r = await fetch("/api/recordings");
  recordings.value = await r.json();
}

async function loadRecordingStatus() {
  const r = await fetch("/api/recordings/status");
  recordingStatus.value = await r.json();
}

async function createRecording(name: string): Promise<Recording> {
  const r = await fetch("/api/recordings", {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ name }),
  });
  const created = await r.json();
  await loadRecordings();
  return created;
}

async function deleteRecording(id: string) {
  await fetch(`/api/recordings/${id}`, { method: "DELETE" });
  recordings.value = recordings.value.filter((r) => r.id !== id);
  await loadRecordingStatus();
}

async function activateRecording(id: string) {
  await fetch(`/api/recordings/${id}/activate`, { method: "POST" });
  await loadRecordings();
  await loadRecordingStatus();
}

async function deactivateRecording(id: string) {
  await fetch(`/api/recordings/${id}/deactivate`, { method: "POST" });
  await loadRecordings();
  await loadRecordingStatus();
}

async function startRecording(name: string) {
  await fetch("/api/recordings/start", {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ name }),
  });
  await loadRecordings();
  await loadRecordingStatus();
}

async function stopRecording() {
  await fetch("/api/recordings/stop", { method: "POST" });
  await loadRecordings();
  await loadRecordingStatus();
}

async function loadRecordingRules(
  recordingId: string,
): Promise<AutoResponderRule[]> {
  const r = await fetch(`/api/recordings/${recordingId}/rules`);
  return r.json();
}

async function updateRecordingRule(
  recordingId: string,
  rule: AutoResponderRule,
) {
  await fetch(`/api/recordings/${recordingId}/rules/${rule.id}`, {
    method: "PUT",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(rule),
  });
}

async function toggleRecordingRule(recordingId: string, ruleId: string) {
  await fetch(`/api/recordings/${recordingId}/rules/${ruleId}/toggle`, {
    method: "POST",
  });
}

async function deleteRecordingRule(recordingId: string, ruleId: string) {
  await fetch(`/api/recordings/${recordingId}/rules/${ruleId}`, {
    method: "DELETE",
  });
}

export function useProxy() {
  return {
    sessions: readonly(sessions),
    rules,
    recordings,
    recordingStatus: readonly(recordingStatus),
    pendingSession,
    connected: readonly(connected),
    connect,
    loadSessions,
    clearSessions,
    loadRules,
    createRule,
    updateRule,
    deleteRule,
    replaySession,
    deleteSessions,
    loadRecordings,
    loadRecordingStatus,
    createRecording,
    deleteRecording,
    activateRecording,
    deactivateRecording,
    startRecording,
    stopRecording,
    loadRecordingRules,
    updateRecordingRule,
    toggleRecordingRule,
    deleteRecordingRule,
  };
}
