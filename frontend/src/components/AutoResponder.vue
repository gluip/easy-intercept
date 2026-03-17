<script setup lang="ts">
import { ref, watch, onMounted } from "vue";
import type { AutoResponderRule } from "../types";
import type { RuleFormData } from "./RuleEditor.vue";
import { useProxy } from "../composables/useProxy";
import RuleEditor from "./RuleEditor.vue";

const { rules, pendingSession, loadRules, createRule, updateRule, deleteRule } =
  useProxy();

const selected = ref<AutoResponderRule | null>(null);
const isNew = ref(false);
const editorRef = ref<InstanceType<typeof RuleEditor> | null>(null);

function selectRule(rule: AutoResponderRule) {
  selected.value = rule;
  isNew.value = false;
}

function handleNew() {
  selected.value = null;
  isNew.value = true;
}

function parseHeaders(text: string): Record<string, string> {
  const headers: Record<string, string> = {};
  for (const line of text.split("\n")) {
    const idx = line.indexOf(":");
    if (idx > 0) {
      headers[line.slice(0, idx).trim()] = line.slice(idx + 1).trim();
    }
  }
  return headers;
}

async function handleSave(data: RuleFormData) {
  const rule = {
    name: data.name,
    method: data.method,
    urlPattern: data.urlPattern,
    bodyPattern: data.bodyPattern,
    bodyPatternIsRegex: data.bodyPatternIsRegex,
    enabled: data.enabled,
    statusCode: data.statusCode,
    contentType: data.contentType,
    headers: parseHeaders(data.headersText),
    body: data.body,
  };

  if (selected.value) {
    const updated = { ...rule, id: selected.value.id } as AutoResponderRule;
    await updateRule(updated);
    selected.value = updated;
  } else {
    const created = await createRule(rule);
    selected.value = created;
    isNew.value = false;
  }
}

async function handleDelete() {
  if (selected.value) {
    await deleteRule(selected.value.id);
    selected.value = null;
    isNew.value = false;
  }
}

function handleToggle(rule: AutoResponderRule) {
  const updated = { ...rule, enabled: !rule.enabled };
  updateRule(updated);
}

function prefillFromSession(session: import("../types").ProxySession) {
  selected.value = null;
  isNew.value = true;
  if (!editorRef.value) return;
  const url = new URL(session.url);
  editorRef.value.form.name = `${session.method} ${url.pathname}`;
  editorRef.value.form.method = session.method;
  editorRef.value.form.urlPattern = url.pathname.replace(
    /[.*+?^${}()|[\]\\]/g,
    "\\$&",
  );
  editorRef.value.form.bodyPattern = "";
  editorRef.value.form.bodyPatternIsRegex = false;
  editorRef.value.form.enabled = true;
  editorRef.value.form.statusCode = session.responseStatus;
  editorRef.value.form.contentType =
    session.responseHeaders["Content-Type"] ??
    session.responseHeaders["content-type"] ??
    "application/json";
  editorRef.value.form.headersText = "";
  editorRef.value.form.body = session.responseBody;
}

watch(pendingSession, (s) => {
  if (s) {
    prefillFromSession(s);
    pendingSession.value = null;
  }
});

onMounted(() => {
  if (pendingSession.value) {
    prefillFromSession(pendingSession.value);
    pendingSession.value = null;
  }
});

loadRules();
</script>

<template>
  <div class="auto-responder">
    <div class="rule-list">
      <div class="list-header">
        <span>Rules</span>
        <button class="new-btn" @click="handleNew">+ New</button>
      </div>
      <div
        v-for="rule in rules"
        :key="rule.id"
        class="rule-item"
        :class="{ selected: selected?.id === rule.id, disabled: !rule.enabled }"
        @click="selectRule(rule)"
      >
        <span
          class="toggle"
          :class="{ on: rule.enabled }"
          @click.stop="handleToggle(rule)"
          :title="rule.enabled ? 'Disable' : 'Enable'"
        >
          {{ rule.enabled ? "●" : "○" }}
        </span>
        <span class="rule-name">{{ rule.name || "(unnamed)" }}</span>
        <span class="rule-method">{{ rule.method }}</span>
        <span class="rule-pattern">{{ rule.urlPattern }}</span>
      </div>
      <div v-if="rules.length === 0" class="empty">No rules yet</div>
    </div>

    <RuleEditor
      v-if="selected || isNew"
      ref="editorRef"
      :rule="selected"
      :is-new="isNew"
      :show-headers="true"
      :show-delete="!!selected"
      :title="isNew ? 'New Rule' : 'Edit Rule'"
      @save="handleSave"
      @delete="handleDelete"
    />

    <div v-else class="editor-placeholder">
      Select a rule to edit or click <strong>+ New</strong> to create one
    </div>
  </div>
</template>

<style scoped>
.auto-responder {
  display: flex;
  flex: 1;
  overflow: hidden;
}

/* --- Rule List --- */
.rule-list {
  width: 320px;
  min-width: 320px;
  border-right: 1px solid #3e3e42;
  overflow-y: auto;
}
.list-header {
  display: flex;
  justify-content: space-between;
  align-items: center;
  padding: 8px 12px;
  background: #252526;
  border-bottom: 1px solid #3e3e42;
  font-size: 12px;
  color: #858585;
  text-transform: uppercase;
  letter-spacing: 0.05em;
  position: sticky;
  top: 0;
}
.new-btn {
  background: #1e3a5f;
  color: #4ec9b0;
  border-color: #4ec9b0;
  font-size: 11px;
  padding: 2px 8px;
}
.new-btn:hover {
  background: #4ec9b0;
  color: #1e1e1e;
}
.rule-item {
  display: flex;
  align-items: center;
  gap: 8px;
  padding: 8px 12px;
  border-bottom: 1px solid #2a2a2a;
  cursor: pointer;
  font-size: 12px;
}
.rule-item:hover {
  background: #2a2d2e;
}
.rule-item.selected {
  background: #094771;
}
.rule-item.disabled {
  opacity: 0.5;
}
.toggle {
  cursor: pointer;
  font-size: 10px;
  width: 14px;
  text-align: center;
}
.toggle.on {
  color: #4ec9b0;
}
.rule-name {
  color: #d4d4d4;
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
  max-width: 120px;
}
.rule-pattern {
  color: #858585;
  font-size: 11px;
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
  flex: 1;
}
.rule-method {
  color: #4ec9b0;
  font-size: 10px;
  background: #1e3a2f;
  padding: 1px 4px;
  border-radius: 2px;
  flex-shrink: 0;
}
.empty {
  padding: 30px;
  color: #555;
  text-align: center;
  font-size: 12px;
}
.editor-placeholder {
  flex: 1;
  color: #555;
  padding: 40px;
  text-align: center;
  font-size: 13px;
}
</style>
