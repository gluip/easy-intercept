<script setup lang="ts">
import { ref, onMounted, onUnmounted } from "vue";

export interface MenuItem {
  label: string;
  icon?: string;
  action: string;
  colors?: { name: string; value: string }[];
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
    <template v-for="item in items" :key="item.action">
      <div v-if="item.colors" class="menu-item colors-item">
        <span v-if="item.icon" class="menu-icon">{{ item.icon }}</span>
        {{ item.label }}
        <div class="color-swatches">
          <button
            v-for="c in item.colors"
            :key="c.name"
            class="swatch"
            :class="{ 'swatch-clear': c.value === '' }"
            :style="c.value ? { background: c.value } : {}"
            :title="c.name"
            @click="handleClick(`${item.action}:${c.value}`)"
          >
            <span v-if="c.value === ''">✕</span>
          </button>
        </div>
      </div>
      <div v-else class="menu-item" @click="handleClick(item.action)">
        <span v-if="item.icon" class="menu-icon">{{ item.icon }}</span>
        {{ item.label }}
      </div>
    </template>
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

.colors-item {
  cursor: default;
}
.colors-item:hover {
  background: none;
}

.color-swatches {
  display: flex;
  gap: 5px;
  margin-left: auto;
}

.swatch {
  width: 14px;
  height: 14px;
  border-radius: 3px;
  border: 1px solid #3e3e42;
  cursor: pointer;
  padding: 0;
  display: flex;
  align-items: center;
  justify-content: center;
  font-size: 9px;
  color: #858585;
  line-height: 1;
}
.swatch:hover {
  border-color: #d4d4d4;
  transform: scale(1.15);
}
.swatch-clear {
  background: #2d2d30;
}
</style>
