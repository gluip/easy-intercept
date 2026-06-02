<script setup lang="ts">
import { ref, computed } from "vue";
import JsonTree from "./JsonTree.vue";

const props = defineProps<{
  data: unknown;
  depth?: number;
  forceOpen?: boolean; // when set, overrides threshold-based default
}>();

const depth = computed(() => props.depth ?? 0);

function getType(val: unknown) {
  if (val === null) return "null";
  if (Array.isArray(val)) return "array";
  return typeof val; // "object" | "string" | "number" | "boolean"
}

const nodeType = computed(() => getType(props.data));
const objKeys = computed(() =>
  nodeType.value === "object"
    ? Object.keys(props.data as Record<string, unknown>)
    : [],
);
const arrLen = computed(() =>
  nodeType.value === "array" ? (props.data as unknown[]).length : 0,
);

function shouldDefaultOpen(): boolean {
  if (props.forceOpen !== undefined) return props.forceOpen;
  if (nodeType.value === "object") return objKeys.value.length <= 3;
  if (nodeType.value === "array") return arrLen.value <= 5;
  return true;
}

const isOpen = ref(shouldDefaultOpen());

// For string values: detect if they're valid JSON
const parsedJson = computed(() => {
  if (nodeType.value !== "string") return null;
  const s = props.data as string;
  if (s.length < 2) return null;
  const t = s.trimStart();
  if (!t.startsWith("{") && !t.startsWith("[")) return null;
  try {
    return JSON.parse(s);
  } catch {
    return null;
  }
});

const isExpandableAsJson = computed(() => parsedJson.value !== null);
const expandedAsJson = ref(false);

// String truncation for very long strings (e.g. base64 blobs)
const MAX_LEN = 200;
const showFull = ref(false);
const strVal = computed(() => props.data as string);
const displayString = computed(() => {
  if (showFull.value || strVal.value.length <= MAX_LEN) return strVal.value;
  return strVal.value.slice(0, MAX_LEN);
});
const isLong = computed(() => strVal.value.length > MAX_LEN);
</script>

<template>
  <!-- null -->
  <span v-if="nodeType === 'null'" class="j-null">null</span>

  <!-- boolean -->
  <span v-else-if="nodeType === 'boolean'" class="j-bool">{{ String(data) }}</span>

  <!-- number -->
  <span v-else-if="nodeType === 'number'" class="j-num">{{ data }}</span>

  <!-- string -->
  <span v-else-if="nodeType === 'string'" class="j-str-wrap">
    <span class="j-str">"{{ displayString }}"</span
    ><span v-if="isLong && !showFull" class="j-ellipsis">…</span
    ><button v-if="isLong && !showFull" class="j-btn" @click.stop="showFull = true"
      >show all</button
    ><button
      v-if="isExpandableAsJson"
      class="j-btn"
      @click.stop="expandedAsJson = !expandedAsJson"
      >{{ expandedAsJson ? "⌃ collapse JSON" : "{ } expand JSON" }}</button
    >
    <div v-if="expandedAsJson && parsedJson !== null" class="j-nested">
      <JsonTree :data="parsedJson" :depth="depth + 1" :force-open="forceOpen" />
    </div>
  </span>

  <!-- object -->
  <span v-else-if="nodeType === 'object'" class="j-complex">
    <span class="j-toggle" @click.stop="isOpen = !isOpen">{{
      isOpen ? "▼" : "▶"
    }}</span
    ><span class="j-brace">{</span>
    <template v-if="!isOpen">
      <span class="j-summary"
        >{{ objKeys.length }} {{ objKeys.length === 1 ? "key" : "keys" }}</span
      ><span class="j-brace">}</span>
    </template>
    <template v-else>
      <div class="j-children">
        <div v-for="(key, i) in objKeys" :key="key" class="j-entry">
          <span class="j-key">"{{ key }}"</span
          ><span class="j-punct">: </span><JsonTree
            :data="(data as Record<string, unknown>)[key]"
            :depth="depth + 1"
            :force-open="forceOpen"
          /><span v-if="i < objKeys.length - 1" class="j-punct">,</span>
        </div>
      </div>
      <div class="j-close">}</div>
    </template>
  </span>

  <!-- array -->
  <span v-else-if="nodeType === 'array'" class="j-complex">
    <span class="j-toggle" @click.stop="isOpen = !isOpen">{{
      isOpen ? "▼" : "▶"
    }}</span
    ><span class="j-brace">[</span>
    <template v-if="!isOpen">
      <span class="j-summary"
        >{{ arrLen }} {{ arrLen === 1 ? "item" : "items" }}</span
      ><span class="j-brace">]</span>
    </template>
    <template v-else>
      <div class="j-children">
        <div v-for="(item, i) in (data as unknown[])" :key="i" class="j-entry">
          <JsonTree
            :data="item"
            :depth="depth + 1"
            :force-open="forceOpen"
          /><span v-if="i < arrLen - 1" class="j-punct">,</span>
        </div>
      </div>
      <div class="j-close">]</div>
    </template>
  </span>
</template>

<style scoped>
/* Complex nodes (object/array) are inline-block so they sit inline
   next to their parent key, while block children extend downward */
.j-complex {
  display: inline-block;
  vertical-align: top;
}

.j-toggle {
  color: #858585;
  cursor: pointer;
  user-select: none;
  font-size: 9px;
  padding: 0 3px 0 0;
  line-height: inherit;
}
.j-toggle:hover {
  color: #d4d4d4;
}

.j-brace {
  color: #d4d4d4;
}
.j-summary {
  color: #6a9955;
  font-style: italic;
  margin: 0 4px;
}
.j-close {
  color: #d4d4d4;
}

.j-children {
  display: block;
  padding-left: 1.5em;
}
.j-entry {
  display: block;
  line-height: 1.6;
  word-break: break-word;
}

.j-key {
  color: #9cdcfe;
}
.j-str {
  color: #ce9178;
  word-break: break-all;
}
.j-num {
  color: #b5cea8;
}
.j-bool {
  color: #569cd6;
}
.j-null {
  color: #569cd6;
}
.j-punct {
  color: #d4d4d4;
}
.j-ellipsis {
  color: #858585;
}

.j-str-wrap {
  display: inline;
}

.j-btn {
  display: inline;
  background: #2a2d2e;
  border: 1px solid #3e3e42;
  color: #9cdcfe;
  cursor: pointer;
  padding: 1px 5px;
  font-size: 10px;
  margin-left: 4px;
  border-radius: 2px;
  font-family: inherit;
  line-height: 1.4;
}
.j-btn:hover {
  background: #3e3e42;
}

.j-nested {
  display: block;
  padding-left: 1.5em;
  border-left: 1px solid #3e3e42;
  margin: 2px 0 2px 0.25em;
}
</style>
