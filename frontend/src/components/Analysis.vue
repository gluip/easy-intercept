<script setup lang="ts">
import { onMounted, onUnmounted, ref, watch } from "vue";
import type { AnalysisEvent, AnalysisEventSummary } from "../types";
import { useProxy } from "../composables/useProxy";

const {
  analysisRuns,
  analysisStatus,
  loadAnalysisRuns,
  loadAnalysisStatus,
  startAnalysis,
  stopAnalysis,
  deleteAnalysisRun,
  loadAnalysisEvents,
  loadAnalysisEvent,
} = useProxy();

const analysisName = ref("Analysis " + new Date().toLocaleTimeString());
const hostFilter = ref("");
const selectedRunId = ref<string | null>(null);
const eventSummaries = ref<AnalysisEventSummary[]>([]);
const selectedSequence = ref<number | null>(null);
const selectedEvent = ref<AnalysisEvent | null>(null);

function lastSequence() {
  return eventSummaries.value.length > 0
    ? eventSummaries.value[eventSummaries.value.length - 1].sequence
    : null;
}

async function refreshRuns() {
  await loadAnalysisRuns();
  await loadAnalysisStatus();
  if (!selectedRunId.value && analysisRuns.value.length > 0) {
    selectedRunId.value = analysisStatus.value.runId ?? analysisRuns.value[0].id;
  }
}

async function refreshEvents(preserveSelection = true) {
  if (!selectedRunId.value) {
    eventSummaries.value = [];
    selectedEvent.value = null;
    return;
  }

  eventSummaries.value = await loadAnalysisEvents(selectedRunId.value);

  if (!preserveSelection || selectedSequence.value === null) {
    selectedSequence.value = lastSequence();
  }

  if (selectedSequence.value !== null) {
    const stillExists = eventSummaries.value.some(
      (eventSummary) => eventSummary.sequence === selectedSequence.value,
    );
    if (!stillExists) {
      selectedSequence.value = lastSequence();
    }
  }

  await refreshSelectedEvent();
}

async function refreshSelectedEvent() {
  if (!selectedRunId.value || selectedSequence.value === null) {
    selectedEvent.value = null;
    return;
  }

  selectedEvent.value = await loadAnalysisEvent(
    selectedRunId.value,
    selectedSequence.value,
  );
}

async function handleStartAnalysis() {
  const run = await startAnalysis(analysisName.value, hostFilter.value);
  selectedRunId.value = run.id;
  selectedSequence.value = null;
  analysisName.value = "Analysis " + new Date().toLocaleTimeString();
  await refreshRuns();
  await refreshEvents(false);
}

async function handleStopAnalysis() {
  await stopAnalysis();
  await refreshRuns();
}

async function handleDeleteRun(id: string) {
  if (selectedRunId.value === id) {
    selectedRunId.value = null;
    selectedSequence.value = null;
    selectedEvent.value = null;
    eventSummaries.value = [];
  }

  await deleteAnalysisRun(id);
  await refreshRuns();
  await refreshEvents(false);
}

function formatTimestamp(timestamp: string) {
  return new Date(timestamp).toLocaleString();
}

let pollHandle: number | null = null;

onMounted(async () => {
  await refreshRuns();
  await refreshEvents(false);

  pollHandle = window.setInterval(async () => {
    if (!analysisStatus.value.runId) return;
    await refreshRuns();
    if (selectedRunId.value === analysisStatus.value.runId) {
      await refreshEvents(true);
    }
  }, 2000);
});

onUnmounted(() => {
  if (pollHandle !== null) {
    window.clearInterval(pollHandle);
  }
});

watch(selectedRunId, async () => {
  selectedSequence.value = null;
  await refreshEvents(false);
});

watch(selectedSequence, async () => {
  await refreshSelectedEvent();
});
</script>

<template>
  <div class="analysis">
    <div class="runs-panel">
      <div class="panel-header">
        <span>Analysis Runs</span>
        <button class="reload-btn" @click="refreshRuns">Reload</button>
      </div>

      <div class="run-controls">
        <template v-if="!analysisStatus.runId">
          <input
            v-model="analysisName"
            class="control-input"
            placeholder="Analysis name…"
            @keydown.enter="handleStartAnalysis"
          />
          <input
            v-model="hostFilter"
            class="control-input"
            placeholder="Host filter, e.g. api.copilot.com"
            @keydown.enter="handleStartAnalysis"
          />
          <button class="start-btn" @click="handleStartAnalysis">Start Analysis</button>
        </template>
        <template v-else>
          <div class="active-run">
            <span class="live-indicator">◎ Analysis active</span>
            <span class="live-filter">
              {{ analysisRuns.find((run) => run.id === analysisStatus.runId)?.hostFilter || "all hosts" }}
            </span>
          </div>
          <button class="stop-btn" @click="handleStopAnalysis">Stop</button>
        </template>
      </div>

      <div v-if="analysisRuns.length === 0" class="empty-state">
        No analysis runs yet.
      </div>

      <div
        v-for="run in analysisRuns"
        :key="run.id"
        class="run-item"
        :class="{ selected: selectedRunId === run.id, active: analysisStatus.runId === run.id }"
        @click="selectedRunId = run.id"
      >
        <div class="run-main">
          <span class="run-name">{{ run.name }}</span>
          <span class="run-count">{{ run.eventCount }} events</span>
        </div>
        <div class="run-meta">
          <span>{{ run.hostFilter || "all hosts" }}</span>
          <span>{{ formatTimestamp(run.createdAt) }}</span>
        </div>
        <div class="run-actions" @click.stop>
          <button class="run-delete" @click="handleDeleteRun(run.id)">Delete</button>
        </div>
      </div>
    </div>

    <div class="events-panel">
      <div class="panel-header">
        <span>Events</span>
        <button class="reload-btn" @click="refreshEvents(true)" :disabled="!selectedRunId">Reload</button>
      </div>

      <div v-if="!selectedRunId" class="empty-state">
        Select a run to inspect events.
      </div>
      <div v-else-if="eventSummaries.length === 0" class="empty-state">
        No events captured yet for this run.
      </div>
      <div
        v-for="eventSummary in eventSummaries"
        :key="eventSummary.sequence"
        class="event-item"
        :class="{ selected: selectedSequence === eventSummary.sequence }"
        @click="selectedSequence = eventSummary.sequence"
      >
        <div class="event-line">
          <span class="event-seq">{{ String(eventSummary.sequence).padStart(6, "0") }}</span>
          <span class="event-method">{{ eventSummary.method }}</span>
          <span class="event-status">{{ eventSummary.responseStatus }}</span>
        </div>
        <div class="event-file">{{ eventSummary.fileName }}</div>
        <div class="event-url">{{ eventSummary.url }}</div>
      </div>
    </div>

    <div class="detail-panel">
      <div class="panel-header">
        <span>Event Detail</span>
      </div>

      <div v-if="!selectedEvent" class="empty-state">
        Select an event to inspect request and response bodies.
      </div>

      <div v-else class="detail-content">
        <div class="detail-meta">
          <div><strong>File</strong> {{ selectedEvent.fileName }}</div>
          <div><strong>Time</strong> {{ formatTimestamp(selectedEvent.timestamp) }}</div>
          <div><strong>URL</strong> {{ selectedEvent.url }}</div>
          <div><strong>Duration</strong> {{ selectedEvent.durationMs }} ms</div>
        </div>

        <div class="body-section">
          <div class="body-header">
            <span>Request</span>
            <span>{{ selectedEvent.requestContentType || "no content-type" }}</span>
          </div>
          <pre class="headers">{{ JSON.stringify(selectedEvent.requestHeaders, null, 2) }}</pre>
          <pre class="body">{{ selectedEvent.requestBody }}</pre>
          <div v-if="selectedEvent.requestBodySkippedReason" class="body-note">
            {{ selectedEvent.requestBodySkippedReason }}
          </div>
        </div>

        <div class="body-section">
          <div class="body-header">
            <span>Response {{ selectedEvent.responseStatus }}</span>
            <span>{{ selectedEvent.responseContentType || "no content-type" }}</span>
          </div>
          <pre class="headers">{{ JSON.stringify(selectedEvent.responseHeaders, null, 2) }}</pre>
          <pre class="body">{{ selectedEvent.responseBody }}</pre>
          <div v-if="selectedEvent.responseBodySkippedReason" class="body-note">
            {{ selectedEvent.responseBodySkippedReason }}
          </div>
        </div>
      </div>
    </div>
  </div>
</template>

<style scoped>
.analysis {
  display: flex;
  flex: 1;
  overflow: hidden;
}

.runs-panel,
.events-panel,
.detail-panel {
  display: flex;
  flex-direction: column;
  overflow: hidden;
}

.runs-panel {
  width: 320px;
  min-width: 320px;
  border-right: 1px solid #3e3e42;
}

.events-panel {
  width: 420px;
  min-width: 420px;
  border-right: 1px solid #3e3e42;
}

.detail-panel {
  flex: 1;
}

.panel-header {
  display: flex;
  justify-content: space-between;
  align-items: center;
  padding: 8px 12px;
  background: #252526;
  border-bottom: 1px solid #3e3e42;
  font-size: 12px;
  color: #858585;
  text-transform: uppercase;
  letter-spacing: 0.05em;
}

.run-controls {
  display: flex;
  flex-direction: column;
  gap: 8px;
  padding: 12px;
  border-bottom: 1px solid #2a2a2a;
}

.control-input {
  width: 100%;
  font-family: inherit;
  font-size: 12px;
  background: #1e1e1e;
  color: #d4d4d4;
  border: 1px solid #555;
  padding: 6px 8px;
}

.start-btn {
  background: #1f4d35;
  color: #9cdcaa;
  border-color: #9cdcaa;
}

.stop-btn {
  background: #5a1d1d;
  color: #f48771;
  border-color: #f48771;
}

.reload-btn,
.run-delete {
  font-size: 11px;
}

.active-run {
  display: flex;
  flex-direction: column;
  gap: 4px;
  font-size: 12px;
}

.live-indicator {
  color: #dcdcaa;
}

.live-filter {
  color: #858585;
}

.run-item,
.event-item {
  padding: 10px 12px;
  border-bottom: 1px solid #2a2a2a;
  cursor: pointer;
}

.run-item:hover,
.event-item:hover {
  background: #2a2d2e;
}

.run-item.selected,
.event-item.selected {
  background: #2f3640;
}

.run-item.active {
  border-left: 3px solid #dcdcaa;
}

.run-main,
.event-line,
.run-meta {
  display: flex;
  justify-content: space-between;
  gap: 8px;
}

.run-main {
  align-items: center;
  margin-bottom: 4px;
}

.run-name,
.event-file {
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.run-count,
.run-meta,
.event-url {
  color: #858585;
  font-size: 11px;
}

.event-seq {
  color: #dcdcaa;
}

.event-method {
  color: #4ec9b0;
  text-transform: uppercase;
}

.event-status {
  color: #d7ba7d;
  margin-left: auto;
}

.empty-state {
  color: #555;
  padding: 20px 16px;
  font-size: 12px;
  text-align: center;
}

.detail-content {
  display: flex;
  flex-direction: column;
  gap: 12px;
  padding: 12px;
  overflow: auto;
}

.detail-meta {
  display: grid;
  gap: 6px;
  font-size: 12px;
}

.body-section {
  border: 1px solid #3e3e42;
  background: #1f1f1f;
}

.body-header {
  display: flex;
  justify-content: space-between;
  gap: 8px;
  padding: 8px 10px;
  border-bottom: 1px solid #3e3e42;
  background: #252526;
  font-size: 12px;
}

.headers,
.body {
  margin: 0;
  padding: 10px;
  overflow: auto;
  white-space: pre-wrap;
  word-break: break-word;
  font-size: 12px;
}

.headers {
  max-height: 180px;
  border-bottom: 1px solid #2a2a2a;
  color: #9cdcfe;
}

.body {
  max-height: 320px;
}

.body-note {
  padding: 0 10px 10px;
  color: #d7ba7d;
  font-size: 11px;
}
</style>