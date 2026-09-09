<script setup lang="ts">
import { computed } from "vue";
import JsonTree from "./JsonTree.vue";
import { unwrapJsonString } from "../utils/json-view";

// Renders a tool call's arguments or a tool result. Both arrive either as a
// structure or as a string holding encoded JSON; the string form is unwrapped
// so it renders as a tree instead of escaped one-liner text.
const props = defineProps<{
  value: unknown;
}>();

const view = computed(() => unwrapJsonString(props.value));
const isText = computed(() => typeof view.value === "string");
</script>

<template>
  <pre v-if="isText" class="payload-text">{{ view }}</pre>
  <div v-else class="payload-tree">
    <JsonTree :data="view" :depth="0" :auto-json="true" />
  </div>
</template>

<style scoped>
.payload-text {
  margin: 0;
  white-space: pre-wrap;
  word-break: break-word;
}

.payload-tree {
  white-space: normal;
  font-family: "Courier New", monospace;
  line-height: 1.5;
}
</style>
