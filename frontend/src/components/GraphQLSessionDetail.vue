<script setup lang="ts">
import { computed, ref } from "vue";
import type { ProxySession } from "../types";
import {
  parseGraphQLRequest,
  parseGraphQLResponse,
  getOperationType,
  getOperationName,
  getTracingDurationMs,
  highlightGraphQL,
  type GraphQLOperation,
  type GraphQLResult,
} from "../utils/graphql-detection";
import SessionHeaders from "./SessionHeaders.vue";

const props = defineProps<{
  session: ProxySession;
}>();

const emit = defineEmits<{
  openViewer: [session: ProxySession, tab: "request" | "response"];
}>();

interface Combined {
  operation: GraphQLOperation;
  result: GraphQLResult | null;
}

const operations = computed(() => parseGraphQLRequest(props.session));
const results = computed(() => parseGraphQLResponse(props.session));

const combined = computed((): Combined[] | null => {
  if (!operations.value) return null;
  return operations.value.map((operation, i) => ({
    operation,
    result: results.value?.[i] ?? null,
  }));
});

const totalErrors = computed(() =>
  (results.value ?? []).reduce((sum, r) => sum + (r.errors?.length ?? 0), 0),
);

// ── Expand / collapse ──────────────────────────────────────

const expandedKeys = ref(new Set<string>());

function toggleKey(key: string) {
  const next = new Set(expandedKeys.value);
  next.has(key) ? next.delete(key) : next.add(key);
  expandedKeys.value = next;
}

function isOpen(key: string) {
  return expandedKeys.value.has(key);
}

function fmtJson(val: unknown): string {
  return JSON.stringify(val, null, 2);
}

function hasVariables(op: GraphQLOperation): boolean {
  return !!op.variables && Object.keys(op.variables).length > 0;
}

function formatTracingDuration(ms: number): string {
  if (ms < 1) return "<1ms";
  if (ms < 1000) return `${Math.round(ms)}ms`;
  return `${(ms / 1000).toFixed(2)}s`;
}
</script>

<template>
  <div class="gql-detail">
    <!-- ── Parse failure ─────────────────────────────────── -->
    <template v-if="!combined">
      <div class="parse-error">
        <span>Could not parse as GraphQL request.</span>
      </div>
    </template>

    <template v-else>
      <!-- ── Stats bar ─────────────────────────────────────── -->
      <div class="stats-bar">
        <span class="op-count">
          {{ combined.length }} operation{{ combined.length === 1 ? "" : "s" }}
        </span>
        <span v-if="totalErrors" class="pill pill-error" title="GraphQL errors">
          ⚠ {{ totalErrors }} error{{ totalErrors === 1 ? "" : "s" }}
        </span>
        <span class="pill pill-duration" title="Request duration">{{ session.durationMs }}ms</span>
        <div class="stats-actions">
          <button class="raw-btn" @click="emit('openViewer', session, 'request')">
            ⬡ Req
          </button>
          <button class="raw-btn" @click="emit('openViewer', session, 'response')">
            ⬡ Res
          </button>
        </div>
      </div>

      <!-- ── Operations ──────────────────────────────────────── -->
      <div class="operations">
        <div v-for="(item, idx) in combined" :key="idx" class="op-card">
          <div class="op-header">
            <span class="op-badge" :class="getOperationType(item.operation.query)">
              {{ getOperationType(item.operation.query) }}
            </span>
            <span class="op-name">{{ getOperationName(item.operation) ?? "(anonymous)" }}</span>
            <span
              v-if="getTracingDurationMs(item.result) !== null"
              class="pill pill-tracing"
              title="Apollo tracing duration (server-side execution time)"
            >
              ⏱ {{ formatTracingDuration(getTracingDurationMs(item.result)!) }}
            </span>
            <span v-if="item.result?.errors?.length" class="error-badge">
              {{ item.result.errors.length }} error{{ item.result.errors.length === 1 ? "" : "s" }}
            </span>
          </div>

          <!-- Variables -->
          <div v-if="hasVariables(item.operation)" class="op-section">
            <div class="section-header" @click="toggleKey(`vars-${idx}`)">
              <span class="section-title">Variables</span>
              <span class="section-toggle">{{ isOpen(`vars-${idx}`) ? "▼" : "▶" }}</span>
            </div>
            <pre v-if="isOpen(`vars-${idx}`)" class="section-body">{{ fmtJson(item.operation.variables) }}</pre>
          </div>

          <!-- Query -->
          <div class="op-section">
            <div class="section-header" @click="toggleKey(`query-${idx}`)">
              <span class="section-title">Query</span>
              <span class="section-toggle">{{ isOpen(`query-${idx}`) ? "▼" : "▶" }}</span>
            </div>
            <pre
              v-if="isOpen(`query-${idx}`)"
              class="section-body gql-source"
              v-html="highlightGraphQL(item.operation.query)"
            ></pre>
          </div>

          <!-- Errors -->
          <div v-if="item.result?.errors?.length" class="op-section errors-section">
            <div class="section-header static">
              <span class="section-title">Errors</span>
            </div>
            <div class="error-list">
              <div v-for="(err, eIdx) in item.result.errors" :key="eIdx" class="error-item">
                <div class="error-message">{{ err.message || "(empty message)" }}</div>
                <div v-if="err.path?.length" class="error-path">path: {{ err.path.join(".") }}</div>
                <pre v-if="err.extensions" class="error-extensions">{{ fmtJson(err.extensions) }}</pre>
              </div>
            </div>
          </div>

          <!-- Response data -->
          <div v-if="item.result && item.result.data !== undefined" class="op-section">
            <div class="section-header" @click="toggleKey(`data-${idx}`)">
              <span class="section-title">Response Data</span>
              <span class="section-toggle">{{ isOpen(`data-${idx}`) ? "▼" : "▶" }}</span>
            </div>
            <pre v-if="isOpen(`data-${idx}`)" class="section-body">{{ fmtJson(item.result.data) }}</pre>
          </div>
        </div>
      </div>

      <!-- ── Headers (collapsed by default) ───────────────────── -->
      <SessionHeaders :headers="session.requestHeaders" label="Request Headers" />
      <SessionHeaders :headers="session.responseHeaders" label="Response Headers" />
    </template>
  </div>
</template>

<style scoped>
.gql-detail {
  display: flex;
  flex-direction: column;
  gap: 0;
}

/* ── Stats bar ──────────────────────────────────────────── */
.stats-bar {
  display: flex;
  align-items: center;
  gap: 8px;
  padding: 8px 10px;
  background: #252526;
  border: 1px solid #3e3e42;
  border-radius: 3px;
  margin-bottom: 10px;
  flex-wrap: wrap;
}

.op-count {
  font-size: 11px;
  font-weight: 600;
  color: #4ec9b0;
  font-family: "Courier New", monospace;
}

.pill {
  font-size: 10px;
  padding: 2px 7px;
  border-radius: 10px;
  font-family: "Courier New", monospace;
}
.pill-duration {
  background: #2a2a2a;
  color: #858585;
  margin-left: auto;
}
.pill-error {
  background: #3a1e1e;
  color: #f44747;
}
.pill-tracing {
  background: #3a2e1e;
  color: #dcb67a;
}

.stats-actions {
  display: flex;
  gap: 4px;
  margin-left: auto;
}

.raw-btn {
  background: transparent;
  color: #569cd6;
  border: 1px solid #3e3e42;
  font-size: 10px;
  padding: 2px 7px;
  border-radius: 3px;
  cursor: pointer;
  font-family: inherit;
}
.raw-btn:hover {
  border-color: #569cd6;
}

/* ── Operations ────────────────────────────────────────────── */
.operations {
  display: flex;
  flex-direction: column;
  gap: 8px;
  margin-bottom: 10px;
}

.op-card {
  border: 1px solid #3e3e42;
  border-radius: 3px;
  overflow: hidden;
  background: #1e1e1e;
}

.op-header {
  display: flex;
  align-items: center;
  gap: 8px;
  padding: 6px 10px;
  background: #252526;
}

.op-badge {
  font-size: 10px;
  font-weight: bold;
  padding: 2px 7px;
  border-radius: 10px;
  text-transform: uppercase;
  letter-spacing: 0.05em;
}
.op-badge.query { background: #1e2d3a; color: #9cdcfe; }
.op-badge.mutation { background: #2d2a1e; color: #dcdcaa; }
.op-badge.subscription { background: #1e3a2f; color: #4ec9b0; }
.op-badge.unknown { background: #2a2a2a; color: #858585; }

.op-name {
  font-size: 12px;
  font-weight: 600;
  color: #d4d4d4;
  font-family: "Courier New", monospace;
  flex: 1;
}

.error-badge {
  font-size: 10px;
  font-weight: 600;
  padding: 2px 7px;
  border-radius: 10px;
  background: #3a1e1e;
  color: #f44747;
}

/* ── Sections ─────────────────────────────────────────────── */
.op-section {
  border-top: 1px solid #3e3e42;
}

.section-header {
  display: flex;
  align-items: center;
  gap: 6px;
  padding: 5px 10px;
  cursor: pointer;
  user-select: none;
  font-size: 11px;
}
.section-header:hover {
  background: #2a2d2e;
}
.section-header.static {
  cursor: default;
}
.section-header.static:hover {
  background: transparent;
}

.section-title {
  color: #858585;
  text-transform: uppercase;
  letter-spacing: 0.05em;
  font-size: 10px;
  font-weight: 600;
  flex: 1;
}

.section-toggle {
  color: #858585;
  font-size: 9px;
}

.section-body {
  margin: 0;
  padding: 8px 10px;
  background: #161616;
  font-size: 11px;
  color: #d4d4d4;
  overflow-x: auto;
  max-height: 320px;
  overflow-y: auto;
  white-space: pre;
  border-top: 1px solid #2a2a2a;
}

/* ── GraphQL syntax highlighting ──────────────────────────── */
.gql-source :deep(.gql-keyword) { color: #c586c0; }
.gql-source :deep(.gql-string) { color: #ce9178; }
.gql-source :deep(.gql-variable) { color: #9cdcfe; }
.gql-source :deep(.gql-directive) { color: #dcdcaa; }
.gql-source :deep(.gql-spread) { color: #4ec9b0; }
.gql-source :deep(.gql-comment) { color: #6a9955; }

/* ── Errors ───────────────────────────────────────────────── */
.errors-section {
  background: #2a1414;
}

.error-list {
  display: flex;
  flex-direction: column;
  gap: 6px;
  padding: 0 10px 8px;
}

.error-item {
  border-left: 2px solid #f44747;
  padding-left: 8px;
}

.error-message {
  font-size: 11px;
  color: #f44747;
}

.error-path {
  font-size: 10px;
  color: #858585;
  font-family: "Courier New", monospace;
  margin-top: 2px;
}

.error-extensions {
  margin: 4px 0 0;
  padding: 6px 8px;
  background: #161616;
  font-size: 10px;
  color: #d4d4d4;
  overflow-x: auto;
  white-space: pre;
  border-radius: 3px;
}

/* ── Parse error ──────────────────────────────────────────── */
.parse-error {
  padding: 20px;
  color: #858585;
  font-size: 12px;
  display: flex;
  align-items: center;
  gap: 10px;
}
</style>
