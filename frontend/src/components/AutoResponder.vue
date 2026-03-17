<script setup lang="ts">
import { ref, reactive, watch, onMounted } from "vue";
import type { AutoResponderRule } from "../types";
import { useProxy } from "../composables/useProxy";

const { rules, pendingSession, loadRules, createRule, updateRule, deleteRule } =
  useProxy();

const selected = ref<AutoResponderRule | null>(null);
const isNew = ref(false);

const form = reactive({
  name: "",
  method: "*",
  urlPattern: "",
  bodyPattern: "",
  bodyPatternIsRegex: false,
  enabled: true,
  statusCode: 200,
  contentType: "application/json",
  headersText: "",
  body: "",
});

function selectRule(rule: AutoResponderRule) {
  selected.value = rule;
  isNew.value = false;
  form.name = rule.name;
  form.method = rule.method;
  form.urlPattern = rule.urlPattern;
  form.bodyPattern = rule.bodyPattern;
  form.bodyPatternIsRegex = rule.bodyPatternIsRegex;
  form.enabled = rule.enabled;
  form.statusCode = rule.statusCode;
  form.contentType = rule.contentType;
  form.headersText = Object.entries(rule.headers)
    .map(([k, v]) => `${k}: ${v}`)
    .join("\n");
  form.body = rule.body;
}

function handleNew() {
  selected.value = null;
  isNew.value = true;
  form.name = "";
  form.method = "*";
  form.urlPattern = "";
  form.bodyPattern = "";
  form.bodyPatternIsRegex = false;
  form.enabled = true;
  form.statusCode = 200;
  form.contentType = "application/json";
  form.headersText = "";
  form.body = "";
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

async function handleSave() {
  const data = {
    name: form.name,
    method: form.method,
    urlPattern: form.urlPattern,
    bodyPattern: form.bodyPattern,
    bodyPatternIsRegex: form.bodyPatternIsRegex,
    enabled: form.enabled,
    statusCode: form.statusCode,
    contentType: form.contentType,
    headers: parseHeaders(form.headersText),
    body: form.body,
  };

  if (selected.value) {
    const updated = { ...data, id: selected.value.id } as AutoResponderRule;
    await updateRule(updated);
    selected.value = updated;
  } else {
    const created = await createRule(data);
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

function formatJson() {
  try {
    form.body = JSON.stringify(JSON.parse(form.body), null, 2);
  } catch {
    /* not valid JSON */
  }
}

function formatXml() {
  let formatted = "";
  let indent = 0;
  const lines = form.body.replace(/>\s*</g, ">\n<").split("\n");
  for (const line of lines) {
    const trimmed = line.trim();
    if (!trimmed) continue;
    if (trimmed.startsWith("</")) indent--;
    formatted += "  ".repeat(Math.max(0, indent)) + trimmed + "\n";
    if (
      trimmed.startsWith("<") &&
      !trimmed.startsWith("</") &&
      !trimmed.startsWith("<?") &&
      !trimmed.endsWith("/>") &&
      !trimmed.includes("</")
    )
      indent++;
  }
  form.body = formatted.trimEnd();
}

function prefillFromSession(session: import("../types").ProxySession) {
  selected.value = null;
  isNew.value = true;
  const url = new URL(session.url);
  form.name = `${session.method} ${url.pathname}`;
  form.method = session.method;
  form.urlPattern = url.pathname.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
  form.bodyPattern = "";
  form.bodyPatternIsRegex = false;
  form.enabled = true;
  form.statusCode = session.responseStatus;
  form.contentType =
    session.responseHeaders["Content-Type"] ??
    session.responseHeaders["content-type"] ??
    "application/json";
  form.headersText = "";
  form.body = session.responseBody;
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

    <div v-if="selected || isNew" class="rule-editor">
      <h3>{{ isNew ? "New Rule" : "Edit Rule" }}</h3>

      <!-- ── Filters ── -->
      <div class="section-label">Match</div>

      <div class="field-row">
        <div class="field">
          <label>Name</label>
          <input v-model="form.name" placeholder="e.g. Mock user API" />
        </div>
        <div class="field" style="flex: 0 0 auto; width: 100px">
          <label>Enabled</label>
          <label class="switch">
            <input type="checkbox" v-model="form.enabled" />
            <span>{{ form.enabled ? "Yes" : "No" }}</span>
          </label>
        </div>
      </div>

      <div class="field-row">
        <div class="field" style="flex: 0 0 auto; width: 110px">
          <label>Method</label>
          <select v-model="form.method">
            <option value="*">Any</option>
            <option>GET</option>
            <option>POST</option>
            <option>PUT</option>
            <option>PATCH</option>
            <option>DELETE</option>
          </select>
        </div>
        <div class="field">
          <label>URL Pattern <small>(regex)</small></label>
          <input
            v-model="form.urlPattern"
            placeholder="e.g. /api/users/\d+"
            spellcheck="false"
          />
        </div>
      </div>

      <div class="field">
        <label>
          Request Body
          <small>(leave empty to skip)</small>
          <span class="format-buttons">
            <button
              class="fmt-btn"
              :class="{ 'fmt-active': form.bodyPatternIsRegex }"
              @click="form.bodyPatternIsRegex = !form.bodyPatternIsRegex"
              :title="form.bodyPatternIsRegex ? 'Regex mode' : 'Contains mode'"
            >
              {{ form.bodyPatternIsRegex ? ".*" : "Aa" }}
            </button>
          </span>
        </label>
        <input
          v-model="form.bodyPattern"
          :placeholder="
            form.bodyPatternIsRegex
              ? 'e.g. action\\s*:\\s*checkout'
              : 'e.g. userId'
          "
          spellcheck="false"
        />
      </div>

      <!-- ── Response ── -->
      <div class="section-label">Response</div>

      <div class="field-row">
        <div class="field" style="flex: 0 0 auto; width: 80px">
          <label>Status</label>
          <input v-model.number="form.statusCode" type="number" class="short" />
        </div>
        <div class="field">
          <label>Content-Type</label>
          <input v-model="form.contentType" />
        </div>
      </div>

      <div class="field">
        <label>Headers <small>(Key: Value per line)</small></label>
        <textarea
          v-model="form.headersText"
          rows="3"
          placeholder="X-Custom: value"
          spellcheck="false"
        ></textarea>
      </div>

      <div class="field">
        <label>
          Body
          <span class="format-buttons">
            <button class="fmt-btn" @click="formatJson" title="Format as JSON">
              JSON
            </button>
            <button class="fmt-btn" @click="formatXml" title="Format as XML">
              XML
            </button>
          </span>
        </label>
        <textarea
          v-model="form.body"
          rows="14"
          placeholder='{"message": "Hello from EasyIntercept"}'
          spellcheck="false"
          class="body-editor"
        ></textarea>
      </div>

      <div class="editor-actions">
        <button class="save-btn" @click="handleSave">
          {{ isNew ? "Create" : "Save" }}
        </button>
        <button v-if="!isNew" class="delete-btn" @click="handleDelete">
          Delete
        </button>
      </div>
    </div>

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

/* --- Editor --- */
.rule-editor {
  flex: 1;
  overflow-y: auto;
  padding: 14px 16px;
  font-size: 12px;
}
.rule-editor h3 {
  font-size: 13px;
  color: #4ec9b0;
  margin-bottom: 14px;
}
.section-label {
  font-size: 11px;
  color: #4ec9b0;
  text-transform: uppercase;
  letter-spacing: 0.06em;
  border-bottom: 1px solid #3e3e42;
  padding-bottom: 4px;
  margin-bottom: 10px;
  margin-top: 14px;
}
.section-label:first-of-type {
  margin-top: 0;
}
.field {
  margin-bottom: 10px;
  flex: 1;
}
.field label {
  display: block;
  color: #858585;
  font-size: 11px;
  text-transform: uppercase;
  letter-spacing: 0.03em;
  margin-bottom: 4px;
}
.field label small {
  text-transform: none;
  letter-spacing: 0;
}
.field input,
.field textarea,
.field select {
  width: 100%;
  background: #252526;
  color: #d4d4d4;
  border: 1px solid #3e3e42;
  padding: 6px 8px;
  font-family: "Cascadia Code", "Fira Code", "Consolas", monospace;
  font-size: 12px;
  border-radius: 3px;
}
.field input:focus,
.field textarea:focus,
.field select:focus {
  outline: none;
  border-color: #007acc;
}
.field input.short {
  width: 80px;
}
.field-row {
  display: flex;
  gap: 12px;
  align-items: flex-start;
}
.switch {
  display: flex;
  align-items: center;
  gap: 6px;
  cursor: pointer;
  color: #d4d4d4;
  text-transform: none;
  letter-spacing: 0;
}
.switch input {
  width: auto;
}
.body-editor {
  resize: vertical;
  min-height: 120px;
}
.format-buttons {
  float: right;
}
.fmt-btn {
  background: #333;
  color: #9cdcfe;
  border: 1px solid #555;
  font-size: 10px;
  padding: 1px 6px;
  margin-left: 4px;
  cursor: pointer;
}
.fmt-btn:hover {
  background: #007acc;
  color: white;
}
.editor-actions {
  display: flex;
  gap: 8px;
  margin-top: 14px;
}
.save-btn {
  background: #1e3a5f;
  color: #4ec9b0;
  border-color: #4ec9b0;
}
.save-btn:hover {
  background: #4ec9b0;
  color: #1e1e1e;
}
.delete-btn {
  background: #3e1e1e;
  color: #f44747;
  border-color: #f44747;
}
.delete-btn:hover {
  background: #f44747;
  color: white;
}
.editor-placeholder {
  flex: 1;
  color: #555;
  padding: 40px;
  text-align: center;
  font-size: 13px;
}
</style>
