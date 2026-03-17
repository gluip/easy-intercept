<script setup lang="ts">
import type { ProxySession } from "../types";

defineProps<{
  sessions: readonly ProxySession[];
  selectedId: string | null;
}>();

const emit = defineEmits<{
  select: [session: ProxySession];
}>();

function methodClass(m: string) {
  return ["GET", "POST", "PUT", "DELETE", "PATCH"].includes(m) ? m : "";
}

function statusClass(s: number) {
  if (s >= 500) return "s5";
  if (s >= 400) return "s4";
  if (s >= 300) return "s3";
  return "s2";
}
</script>

<template>
  <div class="session-list">
    <table>
      <thead>
        <tr>
          <th class="col-method">Method</th>
          <th class="col-status">Status</th>
          <th class="col-url">URL</th>
          <th class="col-dur">ms</th>
        </tr>
      </thead>
      <tbody>
        <tr
          v-for="s in sessions"
          :key="s.id"
          :class="{ selected: s.id === selectedId }"
          @click="emit('select', s)"
        >
          <td class="col-method" :class="methodClass(s.method)">
            {{ s.method }}
          </td>
          <td class="col-status" :class="statusClass(s.responseStatus)">
            {{ s.responseStatus }}
          </td>
          <td class="col-url" :title="s.url">{{ s.url }}</td>
          <td class="col-dur">{{ s.durationMs }}</td>
        </tr>
      </tbody>
    </table>
    <div v-if="sessions.length === 0" class="empty-state">
      No requests yet.<br />
      Configure your browser/app to use proxy <strong>localhost:8888</strong>
    </div>
  </div>
</template>

<style scoped>
.session-list {
  width: 50%;
  overflow-y: auto;
  border-right: 1px solid #3e3e42;
}

table {
  width: 100%;
  border-collapse: collapse;
  font-size: 12px;
}

thead th {
  background: #252526;
  color: #858585;
  text-align: left;
  padding: 6px 10px;
  border-bottom: 1px solid #3e3e42;
  position: sticky;
  top: 0;
}

tbody tr {
  border-bottom: 1px solid #2a2a2a;
  cursor: pointer;
}
tbody tr:hover {
  background: #2a2d2e;
}
tbody tr.selected {
  background: #094771;
}

td {
  padding: 5px 10px;
  white-space: nowrap;
  overflow: hidden;
  max-width: 0;
}

.col-method {
  width: 60px;
  font-weight: bold;
}
.col-status {
  width: 44px;
}
.col-url {
  width: 60%;
  text-overflow: ellipsis;
  overflow: hidden;
}
.col-dur {
  width: 56px;
  text-align: right;
  color: #858585;
}

.GET {
  color: #4ec9b0;
}
.POST {
  color: #dcdcaa;
}
.PUT {
  color: #ce9178;
}
.DELETE {
  color: #f44747;
}
.PATCH {
  color: #c586c0;
}

.s2 {
  color: #4ec9b0;
}
.s3 {
  color: #9cdcfe;
}
.s4 {
  color: #f44747;
}
.s5 {
  color: #ce9178;
}

.empty-state {
  padding: 40px;
  color: #555;
  text-align: center;
  font-size: 13px;
  line-height: 2;
}
</style>
