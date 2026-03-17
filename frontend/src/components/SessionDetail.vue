<script setup lang="ts">
import type { ProxySession } from "../types";

const props = defineProps<{
  session: ProxySession;
}>();

const emit = defineEmits<{
  addAutoResponse: [session: ProxySession];
}>();

function formatJson(obj: unknown): string {
  try {
    if (typeof obj === "string") {
      const parsed = JSON.parse(obj);
      return JSON.stringify(parsed, null, 2);
    }
    return JSON.stringify(obj, null, 2);
  } catch {
    return typeof obj === "string" ? obj : JSON.stringify(obj, null, 2);
  }
}

function isJson(s: string): boolean {
  if (!s) return false;
  const trimmed = s.trimStart();
  return trimmed.startsWith("{") || trimmed.startsWith("[");
}
</script>

<template>
  <div class="detail">
    <h2>{{ session.method }} {{ session.url }}</h2>
    <div class="meta">
      {{ session.responseStatus }} · {{ session.durationMs }}ms ·
      {{ new Date(session.timestamp).toLocaleTimeString() }}
    </div>

    <div class="actions">
      <button class="ar-btn" @click="emit('addAutoResponse', session)">
        ⚡ Add to Auto Responder
      </button>
    </div>

    <h3>Request Headers</h3>
    <pre>{{ formatJson(session.requestHeaders) }}</pre>

    <h3>Request Body</h3>
    <pre>{{
      isJson(session.requestBody)
        ? formatJson(session.requestBody)
        : session.requestBody || "(empty)"
    }}</pre>

    <h3>Response Headers</h3>
    <pre>{{ formatJson(session.responseHeaders) }}</pre>

    <h3>Response Body</h3>
    <pre>{{
      isJson(session.responseBody)
        ? formatJson(session.responseBody)
        : session.responseBody || "(empty)"
    }}</pre>
  </div>
</template>

<style scoped>
.detail {
  width: 50%;
  overflow-y: auto;
  padding: 14px 16px;
  font-size: 12px;
}

h2 {
  font-size: 13px;
  color: #4ec9b0;
  margin-bottom: 10px;
  word-break: break-all;
}

h3 {
  font-size: 11px;
  color: #858585;
  text-transform: uppercase;
  margin: 12px 0 4px;
  letter-spacing: 0.05em;
}

pre {
  background: #252526;
  border: 1px solid #3e3e42;
  padding: 8px;
  overflow-x: auto;
  white-space: pre-wrap;
  word-break: break-all;
  border-radius: 3px;
  max-height: 300px;
}

.meta {
  color: #858585;
  margin-bottom: 10px;
}

.actions {
  display: flex;
  gap: 8px;
  margin: 10px 0;
}

.ar-btn {
  background: #1e3a2f;
  color: #dcdcaa;
  border-color: #dcdcaa;
}
.ar-btn:hover {
  background: #dcdcaa;
  color: #1e1e1e;
}
</style>
