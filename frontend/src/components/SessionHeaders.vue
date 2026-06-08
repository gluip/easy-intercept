<script setup lang="ts">
const props = defineProps<{
  headers: Record<string, string>;
  label: string;
}>();

function formatJson(obj: unknown): string {
  try {
    return JSON.stringify(obj, null, 2);
  } catch {
    return String(obj);
  }
}
</script>

<template>
  <details class="headers-details">
    <summary>{{ props.label }}</summary>
    <pre>{{ formatJson(props.headers) }}</pre>
  </details>
</template>

<style scoped>
.headers-details {
  margin: 12px 0 4px;
}
.headers-details summary {
  font-size: 11px;
  color: #858585;
  text-transform: uppercase;
  letter-spacing: 0.05em;
  cursor: pointer;
  user-select: none;
}
.headers-details summary:hover {
  color: #d4d4d4;
}
.headers-details summary::marker,
.headers-details summary::-webkit-details-marker {
  color: #858585;
}
.headers-details pre {
  margin-top: 6px;
  background: #252526;
  border: 1px solid #3e3e42;
  padding: 8px;
  overflow-x: auto;
  white-space: pre-wrap;
  word-break: break-all;
  font-size: 11px;
  border-radius: 3px;
}
</style>
