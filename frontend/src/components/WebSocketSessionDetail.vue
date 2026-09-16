<script setup lang="ts">
import { computed, ref, watch, nextTick } from "vue";
import type { ProxySession, WebSocketMessage } from "../types";
import SessionHeaders from "./SessionHeaders.vue";
import { isPending } from "../utils/session-state";

const props = defineProps<{
  session: ProxySession;
}>();

const messages = computed(() => props.session.webSocketMessages ?? []);
const isOpen = computed(() => isPending(props.session));
const listEl = ref<HTMLElement>();

// Follow the tail while the socket is open, like a log viewer.
watch(
  () => messages.value.length,
  async () => {
    if (!isOpen.value) return;
    await nextTick();
    listEl.value?.scrollTo({ top: listEl.value.scrollHeight });
  },
);

const counts = computed(() => ({
  sent: messages.value.filter((m) => m.direction === "out" && (m.type === "text" || m.type === "binary")).length,
  received: messages.value.filter((m) => m.direction === "in" && (m.type === "text" || m.type === "binary")).length,
}));

function directionLabel(m: WebSocketMessage): string {
  if (m.type === "note") return "ⓘ";
  if (m.type === "close") return m.direction === "out" ? "→ close" : "← close";
  return m.direction === "out" ? "→ sent" : "← received";
}

function formatOffset(ms: number): string {
  return (ms / 1000).toFixed(3) + "s";
}

function formatData(m: WebSocketMessage): string {
  if (m.type !== "text") return m.data;
  const trimmed = m.data.trimStart();
  if (!trimmed.startsWith("{") && !trimmed.startsWith("[")) return m.data;
  try {
    return JSON.stringify(JSON.parse(m.data), null, 2);
  } catch {
    return m.data;
  }
}
</script>

<template>
  <div class="ws-detail">
    <div class="ws-summary">
      <span class="ws-badge">WS</span>
      <span v-if="isOpen" class="ws-state ws-open">open · live</span>
      <span v-else class="ws-state ws-closed">closed</span>
      <span class="ws-count">{{ counts.sent }} sent · {{ counts.received }} received</span>
    </div>

    <SessionHeaders :headers="session.requestHeaders" label="Handshake Request Headers" />
    <SessionHeaders :headers="session.responseHeaders" label="Handshake Response Headers" />

    <div ref="listEl" class="ws-messages">
      <div v-if="messages.length === 0" class="ws-empty">No messages yet</div>
      <div
        v-for="(m, i) in messages"
        :key="i"
        class="ws-msg"
        :class="['dir-' + m.direction, 'type-' + m.type]"
      >
        <div class="ws-meta">
          <span class="ws-dir">{{ directionLabel(m) }}</span>
          <span class="ws-type">{{ m.type }}</span>
          <span class="ws-time">{{ formatOffset(m.offsetMs) }}</span>
        </div>
        <pre class="ws-data">{{ formatData(m) }}</pre>
      </div>
    </div>
  </div>
</template>

<style scoped>
.ws-detail {
  display: flex;
  flex-direction: column;
  gap: 6px;
}

.ws-summary {
  display: flex;
  align-items: center;
  gap: 10px;
  font-size: 12px;
  color: var(--text-muted, #9aa0a6);
}

.ws-badge {
  padding: 1px 6px;
  border-radius: 3px;
  font-size: 10px;
  font-weight: 700;
  letter-spacing: 0.5px;
  background: #7c3aed;
  color: #fff;
}

.ws-state {
  font-weight: 600;
}

.ws-open {
  color: #22c55e;
  animation: ws-pulse 1.2s ease-in-out infinite;
}

.ws-closed {
  color: var(--text-muted, #9aa0a6);
}

@keyframes ws-pulse {
  0%, 100% { opacity: 1; }
  50% { opacity: 0.4; }
}

.ws-messages {
  max-height: 60vh;
  overflow: auto;
  display: flex;
  flex-direction: column;
  gap: 6px;
  padding: 4px 0;
}

.ws-empty {
  font-size: 12px;
  color: var(--text-muted, #9aa0a6);
  padding: 8px;
}

.ws-msg {
  border-left: 3px solid #7c3aed;
  border-radius: 4px;
  background: rgba(124, 58, 237, 0.06);
  padding: 4px 8px;
}

.ws-msg.dir-out {
  border-left-color: #0ea5e9;
  background: rgba(14, 165, 233, 0.06);
}

.ws-msg.type-close,
.ws-msg.type-note {
  border-left-color: #9aa0a6;
  background: rgba(154, 160, 166, 0.08);
}

.ws-meta {
  display: flex;
  gap: 10px;
  font-size: 11px;
  color: var(--text-muted, #9aa0a6);
  margin-bottom: 2px;
}

.ws-dir {
  font-weight: 600;
}

.ws-data {
  margin: 0;
  white-space: pre-wrap;
  word-break: break-word;
  font-size: 12px;
  max-height: 240px;
  overflow: auto;
}
</style>
