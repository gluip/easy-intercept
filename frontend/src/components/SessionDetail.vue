<script setup lang="ts">
import { computed } from "vue";
import type { ProxySession } from "../types";
import { isLLMRequest } from "../utils/llm-detection";
import { isElasticsearchRequest } from "../utils/es-detection";
import LLMSessionDetail from "./LLMSessionDetail.vue";
import ElasticsearchSessionDetail from "./ElasticsearchSessionDetail.vue";

const props = defineProps<{
  session: ProxySession;
}>();

const emit = defineEmits<{
  addAutoResponse: [session: ProxySession];
  openViewer: [session: ProxySession, tab: "request" | "response"];
}>();

const isLLM = computed(() => isLLMRequest(props.session));
const isES = computed(() => !isLLM.value && isElasticsearchRequest(props.session));

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

function isXml(s: string): boolean {
  if (!s) return false;
  const trimmed = s.trimStart();
  return trimmed.startsWith("<");
}

function formatXml(xml: string): string {
  try {
    const PADDING = "  ";
    const reg = /(>)(<)(\/*)/g;
    let formatted = xml.replace(reg, "$1\r\n$2$3");
    let pad = 0;
    return formatted
      .split("\r\n")
      .map((node) => {
        let indent = 0;
        if (node.match(/.+<\/\w[^>]*>$/)) {
          indent = 0;
        } else if (node.match(/^<\/\w/)) {
          if (pad !== 0) {
            pad -= 1;
          }
        } else if (node.match(/^<\w([^>]*[^\/])?>.*$/)) {
          indent = 1;
        } else {
          indent = 0;
        }
        const padding = PADDING.repeat(pad);
        pad += indent;
        return padding + node;
      })
      .join("\r\n");
  } catch {
    return xml;
  }
}

function formatBody(body: string): string {
  if (isJson(body)) return formatJson(body);
  if (isXml(body)) return formatXml(body);
  return body || "(empty)";
}

function highlightXml(xml: string): string {
  // First add highlighting, then escape everything except our span tags
  let result = xml;
  
  // Match tag names
  result = result.replace(/<\/?([a-zA-Z0-9_:-]+)/g, (match, tagName) => {
    return match.replace(tagName, `##TAG_START##${tagName}##TAG_END##`);
  });
  
  // Match attributes
  result = result.replace(/\s([a-zA-Z0-9_:-]+)=("[^"]*"|'[^']*')/g, (match, attrName, attrValue) => {
    return ` ##ATTR_START##${attrName}##ATTR_END##=##VALUE_START##${attrValue}##VALUE_END##`;
  });
  
  // Now escape HTML
  result = result
    .replace(/&/g, "&amp;")
    .replace(/</g, "&lt;")
    .replace(/>/g, "&gt;");
  
  // Replace markers with actual spans
  result = result
    .replace(/##TAG_START##/g, '<span class="xml-tag">')
    .replace(/##TAG_END##/g, '</span>')
    .replace(/##ATTR_START##/g, '<span class="xml-attr">')
    .replace(/##ATTR_END##/g, '</span>')
    .replace(/##VALUE_START##/g, '<span class="xml-value">')
    .replace(/##VALUE_END##/g, '</span>');
  
  return result;
}

function highlightJson(json: string): string {
  let result = json;
  
  // Mark strings
  result = result.replace(/"([^"\\]|\\.)*"/g, (match) => {
    return `##STRING##${match}##STRING_END##`;
  });
  
  // Mark booleans and null
  result = result.replace(/\b(true|false|null)\b/g, '##BOOL##$1##BOOL_END##');
  
  // Mark numbers
  result = result.replace(/\b(-?\d+\.?\d*([eE][+-]?\d+)?)\b/g, '##NUM##$1##NUM_END##');
  
  // Escape HTML
  result = result
    .replace(/&/g, "&amp;")
    .replace(/</g, "&lt;")
    .replace(/>/g, "&gt;");
  
  // Replace markers - first handle keys (strings followed by :)
  result = result.replace(/##STRING##("(?:[^"\\]|\\.)*")##STRING_END##(\s*):/g, 
    '<span class="json-key">$1</span>$2:');
  
  // Then remaining strings as values
  result = result.replace(/##STRING##/g, '<span class="json-string">').replace(/##STRING_END##/g, '</span>');
  
  // Booleans
  result = result.replace(/##BOOL##/g, '<span class="json-boolean">').replace(/##BOOL_END##/g, '</span>');
  
  // Numbers
  result = result.replace(/##NUM##/g, '<span class="json-number">').replace(/##NUM_END##/g, '</span>');
  
  return result;
}

function formatBodyHtml(body: string): string {
  const formatted = formatBody(body);
  if (isJson(body)) return highlightJson(formatted);
  if (isXml(body)) return highlightXml(formatted);
  return formatted.replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;");
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
      <button class="ar-btn" @click="emit('addAutoResponse', session)">
        ⚡ Add to Auto Responder
      </button>
    </div>

    <!-- LLM request viewer -->
    <LLMSessionDetail
      v-if="isLLM"
      :session="session"
      @open-viewer="(s, t) => emit('openViewer', s, t)"
    />

    <!-- Elasticsearch viewer -->
    <ElasticsearchSessionDetail
      v-else-if="isES"
      :session="session"
    />

    <!-- Standard request viewer -->
    <template v-else>
      <h3>Request Headers</h3>
      <pre>{{ formatJson(session.requestHeaders) }}</pre>

      <div class="section-hdr">
        <h3>Request Body</h3>
        <button class="view-btn" @click="emit('openViewer', session, 'request')">⬡ View</button>
      </div>
      <pre v-html="formatBodyHtml(session.requestBody)"></pre>

      <h3>Response Headers</h3>
      <pre>{{ formatJson(session.responseHeaders) }}</pre>

      <div class="section-hdr">
        <h3>Response Body</h3>
        <button class="view-btn" @click="emit('openViewer', session, 'response')">⬡ View</button>
      </div>
      <pre v-html="formatBodyHtml(session.responseBody)"></pre>
    </template>
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

.ar-btn {
  background: #1e3a2f;
  color: #dcdcaa;
  border-color: #dcdcaa;
}
.ar-btn:hover {
  background: #dcdcaa;
  color: #1e1e1e;
}

.section-hdr {
  display: flex;
  align-items: center;
  justify-content: space-between;
  margin: 12px 0 4px;
}
.section-hdr h3 {
  margin: 0;
}
.view-btn {
  background: #1e2a3f;
  color: #569cd6;
  border-color: #569cd6;
  font-size: 10px;
  padding: 2px 7px;
}
.view-btn:hover {
  background: #569cd6;
  color: #1e1e1e;
}

/* Syntax highlighting */
:deep(.xml-tag) {
  color: #4ec9b0;
}
:deep(.xml-attr) {
  color: #9cdcfe;
}
:deep(.xml-value) {
  color: #ce9178;
}
:deep(.json-key) {
  color: #9cdcfe;
}
:deep(.json-string) {
  color: #ce9178;
}
:deep(.json-number) {
  color: #b5cea8;
}
:deep(.json-boolean) {
  color: #569cd6;
}
</style>
