<script setup lang="ts">
import { ref, onMounted } from "vue";
import type { AutoResponderRule } from "../types";
import type { RuleFormData } from "./RuleEditor.vue";
import { useProxy } from "../composables/useProxy";
import RuleEditor from "./RuleEditor.vue";

const {
  recordings,
  recordingStatus,
  loadRecordings,
  loadRecordingStatus,
  startRecording,
  stopRecording,
  deleteRecording,
  activateRecording,
  deactivateRecording,
  loadRecordingRules,
  updateRecordingRule,
  toggleRecordingRule,
  deleteRecordingRule,
} = useProxy();

const recordName = ref("Recording " + new Date().toLocaleTimeString());
const expandedId = ref<string | null>(null);
const expandedRules = ref<AutoResponderRule[]>([]);
const editingRule = ref<AutoResponderRule | null>(null);

async function handleStartRecording() {
  await startRecording(recordName.value);
  recordName.value = "Recording " + new Date().toLocaleTimeString();
}

async function handleStopRecording() {
  await stopRecording();
}

async function handleToggleActive(id: string, active: boolean) {
  if (active) {
    await deactivateRecording(id);
  } else {
    await activateRecording(id);
  }
}

async function handleDelete(id: string) {
  if (expandedId.value === id) {
    expandedId.value = null;
    expandedRules.value = [];
    editingRule.value = null;
  }
  await deleteRecording(id);
}

async function toggleExpand(id: string) {
  if (expandedId.value === id) {
    expandedId.value = null;
    expandedRules.value = [];
    editingRule.value = null;
    return;
  }
  expandedId.value = id;
  editingRule.value = null;
  expandedRules.value = await loadRecordingRules(id);
}

async function handleToggleRule(ruleId: string) {
  if (!expandedId.value) return;
  await toggleRecordingRule(expandedId.value, ruleId);
  expandedRules.value = await loadRecordingRules(expandedId.value);
  await loadRecordings();
}

async function handleDeleteRule(ruleId: string) {
  if (!expandedId.value) return;
  if (editingRule.value?.id === ruleId) editingRule.value = null;
  await deleteRecordingRule(expandedId.value, ruleId);
  expandedRules.value = await loadRecordingRules(expandedId.value);
  await loadRecordings();
}

function editRule(rule: AutoResponderRule) {
  editingRule.value = rule;
}

async function handleSaveRule(data: RuleFormData) {
  if (!expandedId.value || !editingRule.value) return;
  const updated: AutoResponderRule = {
    id: editingRule.value.id,
    name: data.name,
    method: data.method,
    urlPattern: data.urlPattern,
    bodyPattern: data.bodyPattern,
    bodyPatternIsRegex: data.bodyPatternIsRegex,
    enabled: data.enabled,
    statusCode: data.statusCode,
    contentType: data.contentType,
    headers: {},
    body: data.body,
  };
  await updateRecordingRule(expandedId.value, updated);
  expandedRules.value = await loadRecordingRules(expandedId.value);
  editingRule.value = updated;
}

onMounted(async () => {
  await loadRecordings();
  await loadRecordingStatus();
});
</script>

<template>
  <div class="recordings">
    <div class="recording-list">
      <div class="list-header">
        <span>Recordings</span>
        <div class="record-controls">
          <template v-if="!recordingStatus.recordingId">
            <input
              v-model="recordName"
              placeholder="Recording name…"
              class="record-name"
              @keydown.enter="handleStartRecording"
            />
            <button class="record-btn" @click="handleStartRecording">
              ⏺ Record
            </button>
          </template>
          <template v-else>
            <span class="recording-indicator">⏺ Recording…</span>
            <button class="stop-btn" @click="handleStopRecording">
              ⏹ Stop
            </button>
          </template>
        </div>
      </div>

      <div v-if="recordings.length === 0" class="empty">
        No recordings yet. Click ⏺ Record to start capturing traffic.
      </div>

      <div v-for="rec in recordings" :key="rec.id" class="recording-group">
        <div
          class="recording-item"
          :class="{
            active: rec.active,
            recording: recordingStatus.recordingId === rec.id,
          }"
          @click="toggleExpand(rec.id)"
        >
          <span class="expand-icon">{{
            expandedId === rec.id ? "▾" : "▸"
          }}</span>
          <span class="rec-name">{{ rec.name }}</span>
          <span class="rec-meta">{{ rec.rulesCount }} rules</span>
          <span v-if="rec.active" class="active-badge">▶ Active</span>
          <span v-if="recordingStatus.recordingId === rec.id" class="rec-badge"
            >⏺</span
          >
          <div class="rec-actions" @click.stop>
            <button
              class="activate-btn"
              :class="{ 'is-active': rec.active }"
              @click="handleToggleActive(rec.id, rec.active)"
              :title="rec.active ? 'Deactivate playback' : 'Activate playback'"
            >
              {{ rec.active ? "⏸" : "▶" }}
            </button>
            <button
              class="del-btn"
              @click="handleDelete(rec.id)"
              title="Delete recording"
            >
              ✕
            </button>
          </div>
        </div>

        <div v-if="expandedId === rec.id" class="rules-panel">
          <div v-if="expandedRules.length === 0" class="empty-rules">
            No rules in this recording.
          </div>
          <div
            v-for="rule in expandedRules"
            :key="rule.id"
            class="rule-row"
            :class="{
              disabled: !rule.enabled,
              editing: editingRule?.id === rule.id,
            }"
            @click="editRule(rule)"
          >
            <span
              class="toggle"
              :class="{ on: rule.enabled }"
              @click.stop="handleToggleRule(rule.id)"
              :title="rule.enabled ? 'Disable' : 'Enable'"
            >
              {{ rule.enabled ? "●" : "○" }}
            </span>
            <span class="rule-method">{{ rule.method }}</span>
            <span class="rule-name">{{ rule.name }}</span>
            <span class="rule-status">{{ rule.statusCode }}</span>
            <button
              class="rule-del"
              @click.stop="handleDeleteRule(rule.id)"
              title="Delete rule"
            >
              ✕
            </button>
          </div>
        </div>
      </div>
    </div>

    <RuleEditor
      v-if="editingRule"
      :rule="editingRule"
      :is-new="false"
      :show-headers="false"
      :show-delete="false"
      title="Edit Rule"
      @save="handleSaveRule"
    />

    <div v-else class="editor-placeholder">
      Select a rule within a recording to edit it.
    </div>
  </div>
</template>

<style scoped>
.recordings {
  display: flex;
  flex: 1;
  overflow: hidden;
}

/* --- Recording List --- */
.recording-list {
  width: 420px;
  min-width: 420px;
  border-right: 1px solid #3e3e42;
  overflow-y: auto;
}

.list-header {
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
  position: sticky;
  top: 0;
  z-index: 1;
  gap: 8px;
}

.record-controls {
  display: flex;
  gap: 6px;
  align-items: center;
}

.record-name {
  font-family: inherit;
  font-size: 11px;
  background: #1e1e1e;
  color: #d4d4d4;
  border: 1px solid #555;
  padding: 2px 6px;
  width: 140px;
}

.record-btn {
  background: #5a1d1d;
  color: #f48771;
  border-color: #f48771;
  font-size: 11px;
  padding: 2px 8px;
  white-space: nowrap;
}
.record-btn:hover {
  background: #f48771;
  color: #1e1e1e;
}

.recording-indicator {
  color: #f48771;
  font-size: 11px;
  animation: pulse 1.5s ease-in-out infinite;
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

.stop-btn {
  background: #3e3e42;
  color: #d4d4d4;
  font-size: 11px;
  padding: 2px 8px;
  white-space: nowrap;
}

.empty {
  color: #555;
  padding: 20px 16px;
  font-size: 12px;
  text-align: center;
}

/* --- Recording Item --- */
.recording-item {
  display: flex;
  align-items: center;
  gap: 8px;
  padding: 8px 12px;
  border-bottom: 1px solid #2a2a2a;
  cursor: pointer;
  font-size: 12px;
}
.recording-item:hover {
  background: #2a2d2e;
}
.recording-item.active {
  border-left: 3px solid #4ec9b0;
}
.recording-item.recording {
  border-left: 3px solid #f48771;
}

.expand-icon {
  color: #858585;
  width: 12px;
  flex-shrink: 0;
}

.rec-name {
  flex: 1;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.rec-meta {
  color: #858585;
  font-size: 11px;
  flex-shrink: 0;
}

.active-badge {
  color: #4ec9b0;
  font-size: 10px;
  font-weight: bold;
  flex-shrink: 0;
}

.rec-badge {
  color: #f48771;
  flex-shrink: 0;
  animation: pulse 1.5s ease-in-out infinite;
}

.rec-actions {
  display: flex;
  gap: 4px;
  flex-shrink: 0;
}

.activate-btn {
  background: transparent;
  border: 1px solid #555;
  color: #858585;
  padding: 1px 6px;
  font-size: 11px;
}
.activate-btn:hover {
  color: #4ec9b0;
  border-color: #4ec9b0;
}
.activate-btn.is-active {
  color: #4ec9b0;
  border-color: #4ec9b0;
}

.del-btn {
  background: transparent;
  border: 1px solid #555;
  color: #858585;
  padding: 1px 6px;
  font-size: 11px;
}
.del-btn:hover {
  color: #f48771;
  border-color: #f48771;
}

/* --- Rules Panel --- */
.rules-panel {
  border-bottom: 1px solid #3e3e42;
  background: #1e1e1e;
}

.empty-rules {
  color: #555;
  padding: 10px 20px;
  font-size: 11px;
}

.rule-row {
  display: flex;
  align-items: center;
  gap: 8px;
  padding: 5px 12px 5px 32px;
  font-size: 11px;
  cursor: pointer;
  border-bottom: 1px solid #1a1a1a;
}
.rule-row:hover {
  background: #2a2d2e;
}
.rule-row.editing {
  background: #094771;
}
.rule-row.disabled {
  opacity: 0.5;
}

.toggle {
  cursor: pointer;
  flex-shrink: 0;
}
.toggle.on {
  color: #4ec9b0;
}

.rule-method {
  color: #569cd6;
  width: 50px;
  flex-shrink: 0;
}

.rule-name {
  flex: 1;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.rule-status {
  color: #858585;
  flex-shrink: 0;
}

.rule-del {
  background: transparent;
  border: none;
  color: #555;
  cursor: pointer;
  padding: 0 4px;
  font-size: 11px;
}
.rule-del:hover {
  color: #f48771;
}

.editor-placeholder {
  flex: 1;
  color: #555;
  padding: 40px;
  text-align: center;
  display: flex;
  align-items: center;
  justify-content: center;
}
</style>
