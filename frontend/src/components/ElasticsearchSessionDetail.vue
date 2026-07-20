<script setup lang="ts">
import { computed, ref } from "vue";
import type { ProxySession } from "../types";
import { detectESOperation, parseESIndex, parseFilters } from "../utils/es-detection";
import SessionHeaders from "./SessionHeaders.vue";
import EsBulkLogsDetail from "./EsBulkLogsDetail.vue";

const props = defineProps<{ session: ProxySession }>();

const operation = computed(() => detectESOperation(props.session));
const index = computed(() => parseESIndex(props.session.url));

const reqBody = computed(() => {
  try { return JSON.parse(props.session.requestBody); } catch { return null; }
});

const respBody = computed(() => {
  try { return JSON.parse(props.session.responseBody); } catch { return null; }
});

const filters = computed(() =>
  reqBody.value?.query ? parseFilters(reqBody.value.query) : [],
);

const hits = computed(() => respBody.value?.hits?.hits ?? []);
const totalHits = computed(() => respBody.value?.hits?.total?.value ?? null);
const maxScore = computed(() => respBody.value?.hits?.max_score ?? null);
const took = computed(() => respBody.value?.took ?? null);
const shards = computed(() => respBody.value?._shards ?? null);
const timedOut = computed(() => respBody.value?.timed_out ?? false);

const copied = ref(false);
function copyQuery() {
  const body = reqBody.value;
  if (!body) return;
  let path = "";
  try { path = new URL(props.session.url).pathname; } catch { path = props.session.url; }
  const text = `${props.session.method} ${path}\n${JSON.stringify(body, null, 2)}`;
  navigator.clipboard.writeText(text).then(() => {
    copied.value = true;
    setTimeout(() => (copied.value = false), 1500);
  });
}

const expandedHits = ref<Set<string>>(new Set());
function toggleHit(id: string) {
  if (expandedHits.value.has(id)) expandedHits.value.delete(id);
  else expandedHits.value.add(id);
  expandedHits.value = new Set(expandedHits.value);
}

function pitShort(id: string) {
  return id ? id.substring(0, 24) + "…" : "";
}

const reqSort = computed(() => {
  const s = reqBody.value?.sort;
  if (!s) return null;
  if (Array.isArray(s)) {
    return s.map((item: Record<string, unknown>) => {
      const [field, opts] = Object.entries(item)[0];
      const order = (opts as Record<string, unknown>)?.["order"] ?? "asc";
      return `${field} ${order}`;
    }).join(", ");
  }
  return JSON.stringify(s);
});

function sourceEntries(source: Record<string, unknown>): [string, string][] {
  return Object.entries(source).map(([k, v]) => [
    k,
    typeof v === "object" ? JSON.stringify(v) : String(v),
  ]);
}

</script>

<template>
  <div class="es-detail">

    <!-- Stats bar -->
    <div class="stats-bar">
      <span class="op-badge" :class="operation">{{ operation }}</span>
      <span class="index-name">{{ index || "*" }}</span>
      <template v-if="operation === 'search' && respBody">
        <span class="stat" v-if="totalHits !== null">
          <span class="stat-val">{{ totalHits }}</span> hits
        </span>
        <span class="stat" v-if="took !== null">
          <span class="stat-val">{{ took }}ms</span> ES
        </span>
        <span class="stat" v-if="maxScore !== null">
          <span class="stat-val">{{ (maxScore as number).toFixed(4) }}</span> max score
        </span>
        <span class="stat" v-if="shards">
          <span class="stat-val">{{ shards.successful }}/{{ shards.total }}</span> shards
        </span>
        <span class="stat warn" v-if="timedOut">⚠ timed out</span>
      </template>
      <template v-if="operation === 'pit-create' && respBody">
        <span class="stat"><span class="stat-val">PIT created</span></span>
      </template>
      <template v-if="operation === 'pit-delete' && respBody">
        <span class="stat"><span class="stat-val">{{ respBody.num_freed }}</span> freed</span>
      </template>
      <template v-if="operation === 'bulk'">
        <span class="stat"><span class="stat-val">bulk</span></span>
      </template>
      <template v-if="operation === 'bulk-log'">
        <span class="stat"><span class="stat-val">log ingestion</span></span>
      </template>
      <span class="stat muted">{{ session.durationMs }}ms proxy</span>
    </div>

    <!-- Search detail -->
    <template v-if="operation === 'search' && reqBody">
      <div class="section">
        <div class="section-title">
          Query
          <button class="copy-query-btn" @click.stop="copyQuery" :title="copied ? 'Copied!' : 'Copy request body'">
            {{ copied ? '✓ Copied' : '📋 Copy' }}
          </button>
        </div>
        <div class="filter-list" v-if="filters.length">
          <div class="filter-row" v-for="(f, i) in filters" :key="i">
            <span class="filter-type" :class="f.type">{{ f.type }}</span>
            <span class="filter-field">{{ f.field }}</span>
            <span class="filter-eq">=</span>
            <span class="filter-val">{{ f.value }}</span>
          </div>
        </div>
        <div class="muted small" v-else>No filter clauses parsed</div>

        <div class="query-meta">
          <span v-if="reqBody.size !== undefined">size: <strong>{{ reqBody.size }}</strong></span>
          <span v-if="reqSort">sort: <strong>{{ reqSort }}</strong></span>
          <span v-if="reqBody.search_after">search_after: <strong>{{ reqBody.search_after.join(", ") }}</strong></span>
          <span v-if="reqBody.pit" class="pit-info">PIT: {{ pitShort(reqBody.pit.id) }} keep_alive: {{ reqBody.pit.keep_alive }}</span>
        </div>
      </div>

      <!-- Hits -->
      <div class="section hits-section" v-if="hits.length">
        <div class="section-title">
          Results
          <span class="hit-count">{{ hits.length }} returned{{ totalHits !== null && totalHits > hits.length ? ` of ${totalHits} total` : '' }}</span>
        </div>
        <div class="hit-list">
          <div
            v-for="hit in hits"
            :key="hit._id"
            class="hit-row"
            @click="toggleHit(hit._id)"
          >
            <span class="hit-toggle">{{ expandedHits.has(hit._id) ? '▾' : '▸' }}</span>
            <span class="hit-id">{{ hit._id }}</span>
            <span v-if="hit._score != null" class="hit-score">{{ (hit._score as number).toFixed(4) }}</span>
            <span class="hit-index muted">{{ hit._index }}</span>
            <template v-if="expandedHits.has(hit._id)">
              <div class="hit-source">
                <div class="source-row" v-for="[k, v] in sourceEntries(hit._source)" :key="k">
                  <span class="src-key">{{ k }}</span>
                  <span class="src-val">{{ v }}</span>
                </div>
              </div>
            </template>
          </div>
        </div>
      </div>
      <div class="section muted small" v-else-if="respBody">
        No hits returned.
      </div>
    </template>

    <!-- PIT create -->
    <template v-if="operation === 'pit-create' && respBody">
      <div class="section">
        <div class="section-title">Point In Time ID</div>
        <pre class="pit-id">{{ respBody.id }}</pre>
      </div>
    </template>

    <!-- PIT delete -->
    <template v-if="operation === 'pit-delete'">
      <div class="section">
        <div class="section-title">Result</div>
        <pre>{{ JSON.stringify(respBody, null, 2) }}</pre>
      </div>
    </template>

    <!-- Bulk log ingestion: parsed log entries view -->
    <EsBulkLogsDetail v-if="operation === 'bulk-log'" :session="session" />

    <!-- Bulk / other: raw fallback -->
    <template v-if="operation === 'bulk' || operation === 'other'">
      <div class="section">
        <div class="section-title">Request Body</div>
        <pre>{{ session.requestBody }}</pre>
      </div>
      <div class="section">
        <div class="section-title">Response Body</div>
        <pre>{{ session.responseBody }}</pre>
      </div>
    </template>

    <!-- Headers (collapsed by default) -->
    <div class="section">
      <SessionHeaders :headers="session.requestHeaders" label="Request Headers" />
      <SessionHeaders :headers="session.responseHeaders" label="Response Headers" />
    </div>

  </div>
</template>

<style scoped>
.es-detail {
  flex: 1;
  overflow-y: auto;
  display: flex;
  flex-direction: column;
  gap: 0;
  font-size: 12px;
}

.stats-bar {
  display: flex;
  align-items: center;
  gap: 12px;
  padding: 8px 16px;
  background: #252526;
  border-bottom: 1px solid #3e3e42;
  flex-shrink: 0;
  flex-wrap: wrap;
}

.op-badge {
  font-size: 10px;
  font-weight: bold;
  padding: 2px 7px;
  border-radius: 10px;
  text-transform: uppercase;
  letter-spacing: 0.05em;
}
.op-badge.search    { background: #1e3a2f; color: #4ec9b0; }
.op-badge.pit-create{ background: #1e2d3a; color: #9cdcfe; }
.op-badge.pit-delete{ background: #3a1e1e; color: #f44747; }
.op-badge.bulk      { background: #2d2a1e; color: #dcdcaa; }
.op-badge.bulk-log  { background: #2d1e2a; color: #c586c0; }
.op-badge.other     { background: #2a2a2a; color: #858585; }

.index-name {
  color: #d4d4d4;
  font-family: "Cascadia Code", monospace;
  font-size: 11px;
}

.stat { color: #858585; font-size: 11px; }
.stat-val { color: #4ec9b0; font-weight: bold; }
.stat.warn .stat-val { color: #f44747; }
.muted { color: #858585; }
.small { font-size: 11px; }

.section {
  padding: 12px 16px;
  border-bottom: 1px solid #2a2a2a;
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
}

.hit-count {
  color: #4ec9b0;
  font-size: 10px;
  text-transform: none;
  letter-spacing: 0;
}

/* Filters */
.filter-list {
  display: flex;
  flex-direction: column;
  gap: 3px;
  margin-bottom: 10px;
}
.filter-row {
  display: flex;
  align-items: baseline;
  gap: 6px;
  font-family: "Cascadia Code", monospace;
  font-size: 11px;
}
.filter-type {
  font-size: 9px;
  padding: 1px 5px;
  border-radius: 8px;
  flex-shrink: 0;
  text-transform: uppercase;
  letter-spacing: 0.04em;
}
.filter-type.term     { background: #1e3a2f; color: #4ec9b0; }
.filter-type.terms    { background: #2d2a1e; color: #dcdcaa; }
.filter-type.range    { background: #1e2d3a; color: #9cdcfe; }
.filter-type.match    { background: #2d1e2a; color: #c586c0; }
.filter-type.semantic { background: #2a1e3a; color: #b29ae0; }
.filter-type.knn      { background: #1e2a2d; color: #9cdcfe; }

.filter-field { color: #9cdcfe; }
.filter-eq    { color: #858585; }
.filter-val   { color: #ce9178; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; max-width: 300px; }

.query-meta {
  display: flex;
  gap: 16px;
  flex-wrap: wrap;
  font-size: 11px;
  color: #858585;
  margin-top: 6px;
}
.query-meta strong { color: #d4d4d4; }
.pit-info { font-family: monospace; font-size: 10px; color: #555; }

.copy-query-btn {
  margin-left: auto;
  font-size: 10px;
  padding: 2px 7px;
  background: #1e2d3a;
  color: #9cdcfe;
  border: 1px solid #2a3a4a;
  border-radius: 3px;
  cursor: pointer;
  font-family: inherit;
  text-transform: none;
  letter-spacing: 0;
}
.copy-query-btn:hover { background: #2a3a4a; }

/* Hits */
.hits-section { flex: 1; overflow: hidden; display: flex; flex-direction: column; }
.hit-list {
  overflow-y: auto;
  flex: 1;
  max-height: 400px;
}

.hit-row {
  display: flex;
  flex-wrap: wrap;
  align-items: baseline;
  gap: 8px;
  padding: 5px 8px;
  border-bottom: 1px solid #2a2a2a;
  cursor: pointer;
  font-family: "Cascadia Code", monospace;
  font-size: 11px;
}
.hit-row:hover { background: #2a2d2e; }

.hit-toggle { color: #555; width: 10px; flex-shrink: 0; }
.hit-id { color: #4ec9b0; }
.hit-score { color: #dcdcaa; font-size: 10px; flex-shrink: 0; }
.hit-index { font-size: 10px; }

.hit-source {
  width: 100%;
  margin-top: 4px;
  padding: 8px;
  background: #1a1a1a;
  border-radius: 3px;
  display: grid;
  grid-template-columns: max-content 1fr;
  gap: 2px 12px;
}
.source-row { display: contents; }
.src-key { color: #9cdcfe; font-size: 10px; white-space: nowrap; }
.src-val { color: #ce9178; font-size: 10px; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }

.pit-id {
  font-size: 10px;
  color: #858585;
  word-break: break-all;
  white-space: pre-wrap;
  background: #1a1a1a;
  padding: 8px;
  border-radius: 3px;
}

pre {
  font-size: 11px;
  color: #d4d4d4;
  white-space: pre-wrap;
  word-break: break-word;
  background: #1a1a1a;
  padding: 8px;
  border-radius: 3px;
  max-height: 300px;
  overflow-y: auto;
}
</style>
