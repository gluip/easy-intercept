<script setup lang="ts">
import { ref, watch, onMounted, onUnmounted, shallowRef } from "vue";
import { EditorView, basicSetup } from "codemirror";
import { EditorState } from "@codemirror/state";
import { xml } from "@codemirror/lang-xml";
import { json } from "@codemirror/lang-json";
import { oneDark } from "@codemirror/theme-one-dark";

const props = defineProps<{
  modelValue: string;
  language: "xml" | "json" | "text";
}>();

const emit = defineEmits<{
  "update:modelValue": [value: string];
}>();

const container = ref<HTMLElement | null>(null);
const view = shallowRef<EditorView | null>(null);

function langExtension() {
  if (props.language === "xml") return xml();
  if (props.language === "json") return json();
  return [];
}

function createView(doc: string) {
  if (!container.value) return;
  view.value?.destroy();

  const state = EditorState.create({
    doc,
    extensions: [
      basicSetup,
      oneDark,
      langExtension(),
      EditorView.theme({
        "&": { height: "100%", fontSize: "12px" },
        ".cm-scroller": { overflow: "auto", fontFamily: "'Cascadia Code', 'Fira Code', Consolas, monospace" },
        ".cm-content": { padding: "8px 0" },
      }),
      EditorView.updateListener.of((update) => {
        if (update.docChanged) {
          emit("update:modelValue", update.state.doc.toString());
        }
      }),
    ],
  });

  view.value = new EditorView({ state, parent: container.value });
}

onMounted(() => createView(props.modelValue));
onUnmounted(() => view.value?.destroy());

// Sync external value changes (e.g. auto-format) without re-creating the view
watch(
  () => props.modelValue,
  (newVal) => {
    const current = view.value?.state.doc.toString();
    if (view.value && newVal !== current) {
      view.value.dispatch({
        changes: { from: 0, to: view.value.state.doc.length, insert: newVal },
      });
    }
  },
);

// Recreate view when language changes
watch(() => props.language, () => createView(props.modelValue));
</script>

<template>
  <div ref="container" class="code-editor" />
</template>

<style scoped>
.code-editor {
  flex: 1;
  overflow: hidden;
  border: 1px solid #3e3e42;
  border-radius: 3px;
  display: flex;
  flex-direction: column;
}

.code-editor :deep(.cm-editor) {
  height: 100%;
}

.code-editor :deep(.cm-focused) {
  outline: none;
  border-color: #569cd6;
}
</style>
