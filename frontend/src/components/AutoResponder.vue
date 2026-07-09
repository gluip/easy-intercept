<script setup lang="ts">
import { ref, computed, watch } from "vue";
import type { AutoResponderRule } from "../types";
import CodeEditor from "./CodeEditor.vue";

function detectLanguage(rule: AutoResponderRule): "xml" | "json" | "text" {
  const ct = Object.entries(rule.responseHeaders).find(
    ([k]) => k.toLowerCase() === "content-type",
  )?.[1] ?? "";
  if (ct.includes("xml")) return "xml";
  if (ct.includes("json")) return "json";
  const body = rule.responseBody.trimStart();
  if (body.startsWith("<?xml") || (body.startsWith("<") && !body.startsWith("<!"))) return "xml";
  if (body.startsWith("{") || body.startsWith("[")) return "json";
  return "text";
}

function formatXml(xml: string): string {
  try {
    const doc = new DOMParser().parseFromString(xml, "application/xml");
    if (doc.querySelector("parsererror")) return xml;
    return serializeXml(doc.documentElement, 0);
  } catch {
    return xml;
  }
}

function serializeXml(node: Element, depth: number): string {
  const indent = "  ".repeat(depth);
  const children = Array.from(node.childNodes);
  const hasElementChildren = children.some((c) => c.nodeType === Node.ELEMENT_NODE);

  const attrs = Array.from(node.attributes)
    .map((a) => ` ${a.name}="${a.value}"`)
    .join("");

  if (children.length === 0) return `${indent}<${node.tagName}${attrs} />`;

  if (!hasElementChildren) {
    const text = node.textContent ?? "";
    return `${indent}<${node.tagName}${attrs}>${text}</${node.tagName}>`;
  }

  const inner = children
    .filter((c) => c.nodeType === Node.ELEMENT_NODE)
    .map((c) => serializeXml(c as Element, depth + 1))
    .join("\n");

  return `${indent}<${node.tagName}${attrs}>\n${inner}\n${indent}</${node.tagName}>`;
}

const props = defineProps<{
  rules: readonly AutoResponderRule[];
  pendingRule: AutoResponderRule | null;
}>();

const emit = defineEmits<{
  add: [rule: AutoResponderRule];
  update: [rule: AutoResponderRule];
  delete: [id: string];
  toggle: [id: string];
  pendingConsumed: [];
}>();

const editingRule = ref<AutoResponderRule | null>(null);
const isNew = ref(false);

watch(
  () => props.pendingRule,
  (rule) => {
    if (rule) {
      editingRule.value = { ...rule, responseHeaders: { ...rule.responseHeaders } };
      isNew.value = true;
      emit("pendingConsumed");
    }
  },
  { immediate: true },
);

const headersText = computed({
  get: () =>
    Object.entries(editingRule.value?.responseHeaders ?? {})
      .map(([k, v]) => `${k}: ${v}`)
      .join("\n"),
  set: (text: string) => {
    if (!editingRule.value) return;
    const headers: Record<string, string> = {};
    for (const line of text.split("\n")) {
      const colon = line.indexOf(":");
      if (colon > 0) {
        headers[line.slice(0, colon).trim()] = line.slice(colon + 1).trim();
      }
    }
    editingRule.value.responseHeaders = headers;
  },
});

function startNew() {
  editingRule.value = {
    id: crypto.randomUUID(),
    name: "",
    isEnabled: true,
    method: "GET",
    url: "",
    responseStatus: 200,
    responseHeaders: {},
    responseBody: "",
    latencyMs: 0,
    bodyMatchType: "none",
    bodyMatch: "",
  };
  isNew.value = true;
}

function startEdit(rule: AutoResponderRule) {
  editingRule.value = { ...rule, responseHeaders: { ...rule.responseHeaders } };
  isNew.value = false;
}

const bodyLanguage = computed(() =>
  editingRule.value ? detectLanguage(editingRule.value) : "text",
);

function autoFormat() {
  if (!editingRule.value) return;
  if (bodyLanguage.value === "xml") {
    editingRule.value.responseBody = formatXml(editingRule.value.responseBody);
  } else if (bodyLanguage.value === "json") {
    try {
      editingRule.value.responseBody = JSON.stringify(
        JSON.parse(editingRule.value.responseBody),
        null,
        2,
      );
    } catch {}
  }
}

// Auto-format when loading a rule with structured content
watch(editingRule, (rule) => {
  if (rule && (detectLanguage(rule) === "xml" || detectLanguage(rule) === "json")) {
    autoFormat();
  }
});

function cancelEdit() {
  editingRule.value = null;
}

function saveRule() {
  if (!editingRule.value) return;
  if (isNew.value) {
    emit("add", editingRule.value);
  } else {
    emit("update", editingRule.value);
  }
  editingRule.value = null;
}
</script>

<template>
  <div class="ar-panel">
    <div class="ar-toolbar">
      <span class="ar-title">⚡ Auto Responder</span>
      <button @click="startNew">+ Add Rule</button>
    </div>

    <div class="ar-body">
      <div class="rule-list">
        <div v-if="rules.length === 0" class="empty-state">
          No rules yet.<br />
          Select a captured request and click<br />
          "⚡ Add to Auto Responder".
        </div>
        <div
          v-for="rule in rules"
          :key="rule.id"
          class="rule-row"
          :class="{ disabled: !rule.isEnabled, active: editingRule?.id === rule.id }"
          @click="startEdit(rule)"
        >
          <input
            type="checkbox"
            :checked="rule.isEnabled"
            @click.stop="emit('toggle', rule.id)"
          />
          <span class="rule-method" :class="rule.method">{{ rule.method }}</span>
          <span class="rule-url" :title="rule.url">{{ rule.name || rule.url }}</span>
          <span class="rule-status">{{ rule.responseStatus }}</span>
          <button class="del-btn" @click.stop="emit('delete', rule.id)">✕</button>
        </div>
      </div>

      <div v-if="editingRule" class="rule-editor">
        <div class="editor-header">
          <span>{{ isNew ? "New Rule" : "Edit Rule" }}</span>
          <button class="close-btn" @click="cancelEdit">✕</button>
        </div>

        <label>Name</label>
        <input v-model="editingRule.name" class="field" placeholder="Rule name" />

        <div class="row">
          <div class="field-group">
            <label>Method</label>
            <select v-model="editingRule.method" class="field">
              <option>GET</option>
              <option>POST</option>
              <option>PUT</option>
              <option>PATCH</option>
              <option>DELETE</option>
              <option>HEAD</option>
              <option>OPTIONS</option>
            </select>
          </div>
          <div class="field-group" style="flex: 1">
            <label>URL (exact match)</label>
            <input
              v-model="editingRule.url"
              class="field"
              placeholder="https://api.example.com/endpoint"
            />
          </div>
        </div>

        <div class="row">
          <div class="field-group">
            <label>Body match</label>
            <select v-model="editingRule.bodyMatchType" class="field">
              <option value="none">— none —</option>
              <option value="contains">contains</option>
              <option value="regex">regex</option>
            </select>
          </div>
          <div class="field-group" style="flex:1" v-if="editingRule.bodyMatchType !== 'none'">
            <label>{{ editingRule.bodyMatchType === 'regex' ? 'Pattern' : 'Search text' }}</label>
            <input
              v-model="editingRule.bodyMatch"
              class="field mono"
              :placeholder="editingRule.bodyMatchType === 'regex' ? '(?i)Component.*9763' : 'Component:9763'"
            />
          </div>
        </div>

        <div class="row">
          <div class="field-group">
            <label>Response Status</label>
            <input v-model.number="editingRule.responseStatus" type="number" class="field status-field" />
          </div>
          <div class="field-group">
            <label>Latency (ms)</label>
            <input v-model.number="editingRule.latencyMs" type="number" min="0" class="field status-field" placeholder="0" />
          </div>
        </div>

        <label>Response Headers (one per line: Key: Value)</label>
        <textarea v-model="headersText" class="field mono" rows="4" />

        <div class="body-label-row">
          <label>Response Body</label>
          <span v-if="bodyLanguage !== 'text'" class="lang-badge">{{ bodyLanguage.toUpperCase() }}</span>
          <button class="fmt-btn" @click="autoFormat" title="Auto-format">⇌ Format</button>
        </div>
        <div class="body-editor-wrap">
          <CodeEditor
            v-if="bodyLanguage !== 'text'"
            v-model="editingRule.responseBody"
            :language="bodyLanguage"
          />
          <textarea
            v-else
            v-model="editingRule.responseBody"
            class="field mono body-field"
            rows="12"
          />
        </div>

        <div class="editor-actions">
          <button class="save-btn" @click="saveRule">
            {{ isNew ? "Save Rule" : "Update Rule" }}
          </button>
          <button @click="cancelEdit">Cancel</button>
        </div>
      </div>

      <div v-else class="editor-placeholder">Select a rule to edit, or click "+ Add Rule"</div>
    </div>
  </div>
</template>

<style scoped>
.ar-panel {
  flex: 1;
  display: flex;
  flex-direction: column;
  overflow: hidden;
  background: #1e1e1e;
}

.ar-toolbar {
  background: #252526;
  border-bottom: 1px solid #3e3e42;
  padding: 6px 16px;
  display: flex;
  align-items: center;
  gap: 12px;
  flex-shrink: 0;
}

.ar-title {
  font-size: 12px;
  color: #4ec9b0;
}

.ar-body {
  display: flex;
  flex: 1;
  overflow: hidden;
}

.rule-list {
  width: 420px;
  flex-shrink: 0;
  border-right: 1px solid #3e3e42;
  overflow-y: auto;
}

.rule-row {
  display: flex;
  align-items: center;
  gap: 8px;
  padding: 7px 12px;
  border-bottom: 1px solid #2a2a2a;
  cursor: pointer;
  font-size: 12px;
}
.rule-row:hover {
  background: #2a2d2e;
}
.rule-row.active {
  background: #094771;
}
.rule-row.disabled {
  opacity: 0.45;
}

.rule-method {
  font-weight: bold;
  font-size: 11px;
  width: 52px;
  flex-shrink: 0;
}
.GET { color: #4ec9b0; }
.POST { color: #dcdcaa; }
.PUT { color: #ce9178; }
.DELETE { color: #f44747; }
.PATCH { color: #c586c0; }
.HEAD { color: #9cdcfe; }
.OPTIONS { color: #858585; }

.rule-url {
  flex: 1;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  color: #d4d4d4;
}

.rule-status {
  color: #858585;
  font-size: 11px;
  flex-shrink: 0;
}

.del-btn {
  background: none;
  border: none;
  color: #555;
  cursor: pointer;
  padding: 2px 4px;
  font-size: 11px;
  font-family: inherit;
}
.del-btn:hover {
  color: #f44747;
}

.rule-editor {
  flex: 1;
  overflow-y: auto;
  padding: 16px;
  display: flex;
  flex-direction: column;
  gap: 8px;
  min-height: 0;
}

.editor-header {
  font-size: 13px;
  color: #4ec9b0;
  display: flex;
  justify-content: space-between;
  align-items: center;
  margin-bottom: 8px;
}

label {
  font-size: 11px;
  color: #858585;
  text-transform: uppercase;
  letter-spacing: 0.04em;
}

.field {
  width: 100%;
  background: #3c3c3c;
  border: 1px solid #3e3e42;
  color: #d4d4d4;
  padding: 6px 8px;
  font-size: 12px;
  font-family: inherit;
  border-radius: 3px;
  outline: none;
  box-sizing: border-box;
}
.field:focus {
  border-color: #569cd6;
}

.mono {
  font-family: "Cascadia Code", "Fira Code", "Consolas", monospace;
}

.status-field {
  width: 80px;
}

.body-field {
  resize: vertical;
}

.body-label-row {
  display: flex;
  align-items: center;
  gap: 8px;
}

.lang-badge {
  font-size: 10px;
  color: #4ec9b0;
  background: #1e3a2f;
  padding: 1px 5px;
  border-radius: 8px;
  letter-spacing: 0.05em;
}

.fmt-btn {
  margin-left: auto;
  background: none;
  border: 1px solid #3e3e42;
  color: #858585;
  padding: 2px 8px;
  font-size: 11px;
  cursor: pointer;
  font-family: inherit;
  border-radius: 3px;
}
.fmt-btn:hover {
  color: #d4d4d4;
  border-color: #569cd6;
}

.body-editor-wrap {
  flex: 1;
  min-height: 200px;
  display: flex;
  flex-direction: column;
}

.row {
  display: flex;
  gap: 12px;
}

.field-group {
  display: flex;
  flex-direction: column;
  gap: 4px;
}

.editor-actions {
  display: flex;
  gap: 8px;
  margin-top: 8px;
}

.save-btn {
  background: #1e3a2f;
  color: #4ec9b0;
  border: 1px solid #4ec9b0;
  padding: 6px 16px;
  cursor: pointer;
  font-family: inherit;
  font-size: 12px;
}
.save-btn:hover {
  background: #4ec9b0;
  color: #1e1e1e;
}

.editor-placeholder {
  flex: 1;
  display: flex;
  align-items: center;
  justify-content: center;
  color: #555;
  font-size: 13px;
}

.empty-state {
  padding: 40px;
  color: #555;
  text-align: center;
  font-size: 13px;
  line-height: 1.8;
}

.close-btn {
  background: none;
  border: none;
  color: #858585;
  cursor: pointer;
  font-size: 14px;
  font-family: inherit;
}
.close-btn:hover {
  color: #d4d4d4;
}
</style>
