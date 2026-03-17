<script setup lang="ts">
import { ref, onMounted, onUnmounted } from "vue";

export interface MenuItem {
  label: string;
  icon?: string;
  action: string;
}

const props = defineProps<{
  items: MenuItem[];
  x: number;
  y: number;
}>();

const emit = defineEmits<{
  select: [action: string];
  close: [];
}>();

const el = ref<HTMLElement>();

function handleClick(action: string) {
  emit("select", action);
}

function handleOutside(e: MouseEvent) {
  if (el.value && !el.value.contains(e.target as Node)) {
    emit("close");
  }
}

onMounted(() => {
  setTimeout(() => document.addEventListener("mousedown", handleOutside), 0);
});

onUnmounted(() => {
  document.removeEventListener("mousedown", handleOutside);
});
</script>

<template>
  <div
    ref="el"
    class="context-menu"
    :style="{ left: props.x + 'px', top: props.y + 'px' }"
  >
    <div
      v-for="item in items"
      :key="item.action"
      class="menu-item"
      @click="handleClick(item.action)"
    >
      <span v-if="item.icon" class="menu-icon">{{ item.icon }}</span>
      {{ item.label }}
    </div>
  </div>
</template>

<style scoped>
.context-menu {
  position: fixed;
  z-index: 1000;
  background: #252526;
  border: 1px solid #3e3e42;
  border-radius: 4px;
  box-shadow: 0 4px 12px rgba(0, 0, 0, 0.5);
  min-width: 160px;
  padding: 4px 0;
  font-size: 12px;
}

.menu-item {
  padding: 6px 14px;
  color: #d4d4d4;
  cursor: pointer;
  display: flex;
  align-items: center;
  gap: 8px;
}

.menu-item:hover {
  background: #094771;
}

.menu-icon {
  width: 16px;
  text-align: center;
  font-size: 13px;
}
</style>
