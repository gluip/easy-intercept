<script setup lang="ts">
import { computed, ref } from "vue";
import type { ProxySession } from "../types";
import { parseBulkLogEntries, levelClass } from "../utils/es-bulk-logs";
import JsonTree from "./JsonTree.vue";

const props = defineProps<{ session: ProxySession }>();

const entries = computed(() =>
  parseBulkLogEntries(props.session.requestBody, props.session.responseBody),
);

const levelCounts = computed(() => {
  const counts = new Map<string, number>();
  for (const e of entries.value) {
    const key = e.level ?? "unknown";
    counts.set(key, (counts.get(key) ?? 0) + 1);
  }
  return [...counts.entries()];
});

const statusCounts = computed(() => {
  const counts = new Map<number, number>();
  for (const e of entries.value) {
    if (e.status !== null) counts.set(e.status, (counts.get(e.status) ?? 0) + 1);
  }
  return [...counts.entries()];
});

const expanded = ref<Set<number>>(new Set());
function toggle(i: number) {
  if (expanded.value.has(i)) expanded.value.delete(i);
  else expanded.value.add(i);
  expanded.value = new Set(expanded.value);
}

function formatTime(ts: string | null): string {
  if (!ts) return "";
  const d = new Date(ts);
  if (isNaN(d.getTime())) return ts;
  const pad = (n: number, w = 2) => String(n).padStart(w, "0");
  return `${pad(d.getHours())}:${pad(d.getMinutes())}:${pad(d.getSeconds())}.${pad(d.getMilliseconds(), 3)}`;
}

function customEntries(data: Record<string, unknown>): [string, string][] {
  return Object.entries(data).map(([k, v]) => [
    k,
    typeof v === "object" && v !== null ? JSON.stringify(v) : String(v),
  ]);
}

function statusClass(status: number | null): string {
  if (status === null) return "none";
  if (status >= 200 && status < 300) return "ok";
  if (status >= 400) return "error";
  return "warn";
}
</script>

<template>
  <div class="bulk-logs" v-if="entries.length">
    <div class="section-title">
      Log entries
      <span class="entry-count">{{ entries.length }}</span>
      <span
        v-for="[level, count] in levelCounts"
        :key="level"
        class="level-badge"
        :class="levelClass(level)"
      >{{ count }} × {{ level }}</span>
      <span
        v-for="[status, count] in statusCounts"
        :key="status"
        class="status-badge"
        :class="statusClass(status)"
      >{{ count }} × {{ status }}</span>
    </div>

    <div class="entry-list">
      <div v-for="(entry, i) in entries" :key="i" class="entry">
        <div class="entry-row" @click="toggle(i)">
          <span class="toggle">{{ expanded.has(i) ? "▾" : "▸" }}</span>
          <span class="level-badge" :class="levelClass(entry.level)">{{ entry.level ?? entry.action }}</span>
          <span class="time">{{ formatTime(entry.timestamp) }}</span>
          <span class="source" v-if="entry.source">{{ entry.source }}</span>
          <span class="row-message">{{ entry.displayMessage }}</span>
          <span
            v-if="entry.status !== null"
            class="status-badge"
            :class="statusClass(entry.status)"
          >{{ entry.status }}</span>
        </div>

        <div v-if="expanded.has(i)" class="entry-detail">
          <div class="field-grid">
            <span class="field-key">level</span>
            <span class="field-val">
              <span class="level-badge" :class="levelClass(entry.level)">{{ entry.level ?? "?" }}</span>
            </span>

            <span class="field-key">message</span>
            <span class="field-val message-val">{{ entry.displayMessage || "(empty)" }}</span>

            <template v-if="entry.logger">
              <span class="field-key">logger</span>
              <span class="field-val">
                {{ entry.logger }}<template v-if="entry.wrapperLogger && entry.wrapperLogger !== entry.logger"> · {{ entry.wrapperLogger }}</template>
              </span>
            </template>

            <template v-if="entry.source">
              <span class="field-key">source</span>
              <span class="field-val">{{ entry.source }}</span>
            </template>

            <template v-if="entry.timestamp">
              <span class="field-key">timestamp</span>
              <span class="field-val">{{ entry.timestamp }}</span>
            </template>

            <template v-if="entry.status !== null">
              <span class="field-key">result</span>
              <span class="field-val">
                <span class="status-badge" :class="statusClass(entry.status)">{{ entry.status }}</span>
                <span v-if="entry.responseError" class="resp-error"> {{ entry.responseError }}</span>
              </span>
            </template>
          </div>

          <div v-if="entry.customData" class="custom-section">
            <div class="sub-title">customData</div>
            <div class="custom-grid">
              <template v-for="[k, v] in customEntries(entry.customData)" :key="k">
                <span class="custom-key">{{ k }}</span>
                <span class="custom-val">{{ v }}</span>
              </template>
            </div>
          </div>

          <div v-if="entry.exception" class="exception-section">
            <div class="sub-title error-title">exception</div>
            <pre class="exception-pre">{{ entry.exception }}</pre>
          </div>

          <details v-if="entry.stackTrace" class="collapse">
            <summary>Stack trace</summary>
            <pre class="trace-pre">{{ entry.stackTrace }}</pre>
          </details>

          <details v-if="entry.message && entry.message !== entry.displayMessage" class="collapse">
            <summary>Full message</summary>
            <pre class="trace-pre">{{ entry.message }}</pre>
          </details>

          <details v-if="entry.doc" class="collapse">
            <summary>Raw document</summary>
            <div class="raw-json">
              <JsonTree :data="entry.doc" />
            </div>
          </details>
        </div>
      </div>
    </div>
  </div>

  <!-- Fallback when the bulk body isn't parseable as log entries -->
  <template v-else>
    <div class="fallback">
      <div class="section-title">Request Body</div>
      <pre class="trace-pre">{{ session.requestBody }}</pre>
      <div class="section-title">Response Body</div>
      <pre class="trace-pre">{{ session.responseBody }}</pre>
    </div>
  </template>
</template>

<style scoped>
.bulk-logs,
.fallback {
  padding: 12px 16px;
  border-bottom: 1px solid #2a2a2a;
  font-size: 12px;
}

.section-title {
  font-size: 11px;
  color: #858585;
  text-transform: uppercase;
  letter-spacing: 0.05em;
  margin-bottom: 8px;
  display: flex;
  align-items: center;
  gap: 8px;
  flex-wrap: wrap;
}

.entry-count {
  color: #4ec9b0;
  font-weight: bold;
}

.entry-list {
  display: flex;
  flex-direction: column;
}

.entry {
  border-bottom: 1px solid #2a2a2a;
}
.entry:last-child {
  border-bottom: none;
}

.entry-row {
  display: flex;
  align-items: baseline;
  gap: 8px;
  padding: 5px 8px;
  cursor: pointer;
  font-family: "Cascadia Code", monospace;
  font-size: 11px;
  min-width: 0;
}
.entry-row:hover {
  background: #2a2d2e;
}

.toggle {
  color: #555;
  width: 10px;
  flex-shrink: 0;
}

.time {
  color: #858585;
  flex-shrink: 0;
}

.source {
  color: #9cdcfe;
  flex-shrink: 0;
  max-width: 280px;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.row-message {
  color: #d4d4d4;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  flex: 1;
  min-width: 60px;
}

.level-badge {
  font-size: 9px;
  font-weight: bold;
  padding: 1px 6px;
  border-radius: 8px;
  text-transform: uppercase;
  letter-spacing: 0.04em;
  flex-shrink: 0;
}
.level-badge.info    { background: #1e2d3a; color: #569cd6; }
.level-badge.warning { background: #2d2a1e; color: #dcdcaa; }
.level-badge.error   { background: #3a1e1e; color: #f44747; }
.level-badge.debug   { background: #2a2a2a; color: #858585; }
.level-badge.none    { background: #2a2a2a; color: #858585; }

.status-badge {
  font-size: 9px;
  font-weight: bold;
  padding: 1px 6px;
  border-radius: 8px;
  flex-shrink: 0;
}
.status-badge.ok    { background: #1e3a2f; color: #4ec9b0; }
.status-badge.warn  { background: #2d2a1e; color: #dcdcaa; }
.status-badge.error { background: #3a1e1e; color: #f44747; }
.status-badge.none  { background: #2a2a2a; color: #858585; }

.entry-detail {
  padding: 8px 8px 10px 26px;
  background: #1a1a1a;
  border-radius: 3px;
  margin: 0 4px 6px;
  display: flex;
  flex-direction: column;
  gap: 10px;
}

.field-grid {
  display: grid;
  grid-template-columns: max-content 1fr;
  gap: 4px 14px;
  font-family: "Cascadia Code", monospace;
  font-size: 11px;
}
.field-key {
  color: #858585;
  white-space: nowrap;
}
.field-val {
  color: #d4d4d4;
  word-break: break-word;
}
.message-val {
  color: #ce9178;
}
.resp-error {
  color: #f44747;
}

.sub-title {
  font-size: 10px;
  color: #858585;
  text-transform: uppercase;
  letter-spacing: 0.05em;
  margin-bottom: 4px;
}
.error-title {
  color: #f44747;
}

.custom-grid {
  display: grid;
  grid-template-columns: max-content 1fr;
  gap: 2px 14px;
  font-family: "Cascadia Code", monospace;
  font-size: 11px;
  background: #212121;
  border: 1px solid #2e2e2e;
  border-radius: 3px;
  padding: 6px 8px;
}
.custom-key {
  color: #9cdcfe;
  white-space: nowrap;
}
.custom-val {
  color: #ce9178;
  word-break: break-word;
}

.exception-pre {
  font-size: 11px;
  color: #f48771;
  white-space: pre-wrap;
  word-break: break-word;
  background: #2a1e1e;
  border: 1px solid #5a1d1d;
  padding: 8px;
  border-radius: 3px;
  max-height: 200px;
  overflow-y: auto;
  margin: 0;
}

.collapse summary {
  font-size: 10px;
  color: #858585;
  text-transform: uppercase;
  letter-spacing: 0.05em;
  cursor: pointer;
  user-select: none;
}
.collapse summary:hover {
  color: #d4d4d4;
}

.trace-pre {
  font-size: 10px;
  color: #a0a0a0;
  white-space: pre-wrap;
  word-break: break-word;
  background: #212121;
  padding: 8px;
  border-radius: 3px;
  max-height: 300px;
  overflow-y: auto;
  margin: 4px 0 0;
}

.raw-json {
  font-family: "Cascadia Code", monospace;
  font-size: 11px;
  background: #212121;
  padding: 8px;
  border-radius: 3px;
  margin-top: 4px;
  max-height: 400px;
  overflow: auto;
}
</style>
