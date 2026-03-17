<script setup lang="ts">
import type { ProxySession } from "../types";

const props = defineProps<{
  session: ProxySession;
  pinned: boolean;
}>();

const emit = defineEmits<{
  pin: [id: string];
  unpin: [url: string];
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
      <button
        v-if="pinned"
        class="unpin-btn"
        @click="emit('unpin', session.url)"
      >
        📌 Unpin response
      </button>
      <button v-else class="pin-btn" @click="emit('pin', session.id)">
        📌 Pin response
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

.pin-btn {
  background: #1e3a5f;
  color: #4ec9b0;
  border-color: #4ec9b0;
}
.pin-btn:hover {
  background: #4ec9b0;
  color: #1e1e1e;
}

.unpin-btn {
  background: #3e1e1e;
  color: #f44747;
  border-color: #f44747;
}
.unpin-btn:hover {
  background: #f44747;
  color: white;
}
</style>
