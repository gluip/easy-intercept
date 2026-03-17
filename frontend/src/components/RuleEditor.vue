<script setup lang="ts">
import { reactive, watch } from "vue";
import type { AutoResponderRule } from "../types";

export interface RuleFormData {
  name: string;
  method: string;
  urlPattern: string;
  bodyPattern: string;
  bodyPatternIsRegex: boolean;
  enabled: boolean;
  statusCode: number;
  contentType: string;
  headersText: string;
  body: string;
}

const props = withDefaults(
  defineProps<{
    rule?: AutoResponderRule | null;
    isNew?: boolean;
    showHeaders?: boolean;
    showDelete?: boolean;
    title?: string;
  }>(),
  {
    rule: null,
    isNew: false,
    showHeaders: true,
    showDelete: false,
    title: "Edit Rule",
  },
);

const emit = defineEmits<{
  save: [data: RuleFormData];
  delete: [];
}>();

const form = reactive<RuleFormData>({
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

function loadRule(rule: AutoResponderRule | null | undefined) {
  if (!rule) {
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
    return;
  }
  form.name = rule.name;
  form.method = rule.method;
  form.urlPattern = rule.urlPattern;
  form.bodyPattern = rule.bodyPattern;
  form.bodyPatternIsRegex = rule.bodyPatternIsRegex;
  form.enabled = rule.enabled;
  form.statusCode = rule.statusCode;
  form.contentType = rule.contentType;
  form.headersText = rule.headers
    ? Object.entries(rule.headers)
        .map(([k, v]) => `${k}: ${v}`)
        .join("\n")
    : "";
  form.body = rule.body;
}

watch(() => props.rule, loadRule, { immediate: true });

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

function handleSave() {
  emit("save", { ...form });
}

function handleDelete() {
  emit("delete");
}

defineExpose({ form, loadRule });
</script>

<template>
  <div class="rule-editor">
    <h3>{{ title }}</h3>

    <!-- ── Match ── -->
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

    <div v-if="showHeaders" class="field">
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
      <button v-if="showDelete" class="delete-btn" @click="handleDelete">
        Delete
      </button>
    </div>
  </div>
</template>

<style scoped>
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
.fmt-active {
  color: #4ec9b0;
  border-color: #4ec9b0;
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
</style>
