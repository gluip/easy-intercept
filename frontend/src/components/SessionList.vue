<script setup lang="ts">
import { ref, computed, onMounted, onUnmounted } from "vue";
import type { ProxySession } from "../types";
import ContextMenu from "./ContextMenu.vue";

const props = defineProps<{
  sessions: readonly ProxySession[];
  selectedIds: string[];
}>();

const emit = defineEmits<{
  select: [ids: string[]];
  copyUrl: [session: ProxySession];
  replay: [session: ProxySession];
  addAutoResponse: [session: ProxySession];
  deleteSelected: [ids: string[]];
}>();

const listEl = ref<HTMLElement>();
const lastClickedId = ref<string | null>(null);

const ctxMenu = ref<{ session: ProxySession; x: number; y: number } | null>(
  null,
);

const selectedSet = computed(() => new Set(props.selectedIds));

const menuItems = [
  { label: "Copy URL", icon: "📋", action: "copy-url" },
  { label: "Replay", icon: "🔁", action: "replay" },
  { label: "Add to Auto Responder", icon: "⚡", action: "add-auto-response" },
  { label: "Delete", icon: "🗑️", action: "delete" },
];

function handleClick(e: MouseEvent, session: ProxySession) {
  const meta = e.metaKey || e.ctrlKey;
  const shift = e.shiftKey;

  if (shift && lastClickedId.value) {
    // range select
    const ids = props.sessions.map((s) => s.id);
    const from = ids.indexOf(lastClickedId.value);
    const to = ids.indexOf(session.id);
    if (from >= 0 && to >= 0) {
      const [lo, hi] = from < to ? [from, to] : [to, from];
      const range = ids.slice(lo, hi + 1);
      if (meta) {
        const merged = new Set([...props.selectedIds, ...range]);
        emit("select", [...merged]);
      } else {
        emit("select", range);
      }
    }
  } else if (meta) {
    // toggle single
    if (selectedSet.value.has(session.id)) {
      emit(
        "select",
        props.selectedIds.filter((id) => id !== session.id),
      );
    } else {
      emit("select", [...props.selectedIds, session.id]);
    }
  } else {
    emit("select", [session.id]);
  }
  lastClickedId.value = session.id;
}

function onContextMenu(e: MouseEvent, session: ProxySession) {
  e.preventDefault();
  // if right-clicked session not in selection, select it
  if (!selectedSet.value.has(session.id)) {
    emit("select", [session.id]);
    lastClickedId.value = session.id;
  }
  ctxMenu.value = { session, x: e.clientX, y: e.clientY };
}

function onMenuSelect(action: string) {
  if (!ctxMenu.value) return;
  const session = ctxMenu.value.session;
  ctxMenu.value = null;
  if (action === "copy-url") emit("copyUrl", session);
  else if (action === "replay") emit("replay", session);
  else if (action === "add-auto-response") emit("addAutoResponse", session);
  else if (action === "delete") emit("deleteSelected", [...props.selectedIds]);
}

function onKeyDown(e: KeyboardEvent) {
  // only handle when our list is focused
  if (
    !listEl.value?.contains(document.activeElement) &&
    document.activeElement !== listEl.value
  )
    return;

  if (
    (e.key === "Delete" || e.key === "Backspace") &&
    props.selectedIds.length > 0
  ) {
    e.preventDefault();
    emit("deleteSelected", [...props.selectedIds]);
  }
  if ((e.metaKey || e.ctrlKey) && e.key === "a") {
    e.preventDefault();
    emit(
      "select",
      props.sessions.map((s) => s.id),
    );
  }
  if (e.key === "ArrowDown" || e.key === "ArrowUp") {
    e.preventDefault();
    const ids = props.sessions.map((s) => s.id);
    if (ids.length === 0) return;
    const lastSelected =
      props.selectedIds.length > 0
        ? props.selectedIds[props.selectedIds.length - 1]
        : null;
    const curIdx = lastSelected ? ids.indexOf(lastSelected) : -1;
    const nextIdx =
      e.key === "ArrowDown"
        ? Math.min(curIdx + 1, ids.length - 1)
        : Math.max(curIdx - 1, 0);
    const nextId = ids[nextIdx];
    if (e.shiftKey) {
      if (!props.selectedIds.includes(nextId)) {
        emit("select", [...props.selectedIds, nextId]);
      } else {
        // shrink selection when going back
        emit("select", props.selectedIds.filter((id) => id !== lastSelected));
      }
    } else {
      emit("select", [nextId]);
    }
    lastClickedId.value = nextId;
    // scroll the row into view
    const row = listEl.value?.querySelector(`tr[data-id="${nextId}"]`);
    row?.scrollIntoView({ block: "nearest" });
  }
}

onMounted(() => document.addEventListener("keydown", onKeyDown));
onUnmounted(() => document.removeEventListener("keydown", onKeyDown));

function methodClass(m: string) {
  return ["GET", "POST", "PUT", "DELETE", "PATCH"].includes(m) ? m : "";
}

function statusClass(s: number) {
  if (s >= 500) return "s5";
  if (s >= 400) return "s4";
  if (s >= 300) return "s3";
  return "s2";
}

function isAutoResponse(s: ProxySession) {
  return s.responseHeaders?.["X-EasyIntercept-AutoResponder"] === "true";
}
</script>

<template>
  <div ref="listEl" class="session-list" tabindex="0">
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
          :data-id="s.id"
          :class="{ selected: selectedSet.has(s.id) }"
          @click="handleClick($event, s)"
          @contextmenu="onContextMenu($event, s)"
        >
          <td class="col-method" :class="methodClass(s.method)">
            {{ s.method }}
          </td>
          <td class="col-status" :class="statusClass(s.responseStatus)">
            {{ s.responseStatus }}
          </td>
          <td class="col-url" :title="s.url">
            <span
              v-if="isAutoResponse(s)"
              class="ar-badge"
              title="Auto Responder"
              >⚡</span
            >
            {{ s.url }}
          </td>
          <td class="col-dur">{{ s.durationMs }}</td>
        </tr>
      </tbody>
    </table>
    <div v-if="sessions.length === 0" class="empty-state">
      No requests yet.<br />
      Configure your browser/app to use proxy <strong>localhost:8888</strong>
    </div>

    <ContextMenu
      v-if="ctxMenu"
      :items="menuItems"
      :x="ctxMenu.x"
      :y="ctxMenu.y"
      @select="onMenuSelect"
      @close="ctxMenu = null"
    />
  </div>
</template>

<style scoped>
.session-list {
  width: 50%;
  overflow-y: auto;
  border-right: 1px solid #3e3e42;
  outline: none;
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

.ar-badge {
  margin-right: 4px;
  font-size: 11px;
}
</style>
