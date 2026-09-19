<script setup lang="ts">
import { computed, ref } from "vue";
import { formatBytes, mediaFileName, type LLMMedia } from "../utils/llm-images";

const props = defineProps<{
  media: LLMMedia;
  index: number; // position in the turn, used for the download file name
}>();

// Thumbnail by default; a click shows the image at its natural size
const zoomed = ref(false);

const fileName = computed(() => mediaFileName(props.media, props.index));
</script>

<template>
  <div class="part-media">
    <div class="media-header">
      <span class="media-icon">{{ media.isImage ? "🖼" : "📎" }}</span>
      <span class="media-mime">{{ media.mimeType }}</span>
      <span v-if="media.inline" class="media-size">{{ formatBytes(media.bytes) }}</span>
      <a
        v-if="media.inline"
        class="media-download"
        :href="media.src"
        :download="fileName"
        @click.stop
      >⬇ Download</a>
    </div>
    <img
      v-if="media.isImage"
      :class="['media-image', { 'media-image-zoomed': zoomed }]"
      :src="media.src"
      :alt="`${media.mimeType} image`"
      :title="zoomed ? 'Click to shrink' : 'Click for full size'"
      @click="zoomed = !zoomed"
    />
    <div v-else-if="!media.inline" class="media-uri">{{ media.src }}</div>
  </div>
</template>

<style scoped>
.part-media {
  border: 1px solid #3e3e42;
  border-radius: 3px;
  background: #1e1e1e;
  overflow: hidden;
}

.media-header {
  display: flex;
  align-items: center;
  gap: 8px;
  padding: 4px 8px;
  background: #2a2d2e;
  font-size: 11px;
}
.media-mime {
  color: #9cdcfe;
  font-family: "Cascadia Code", "Consolas", monospace;
}
.media-size {
  color: #858585;
}
.media-download {
  margin-left: auto;
  color: #9cdcfe;
  text-decoration: none;
  border: 1px solid #3e3e42;
  border-radius: 2px;
  padding: 1px 6px;
}
.media-download:hover {
  background: #3e3e42;
}

.media-image {
  display: block;
  max-width: calc(100% - 16px);
  max-height: 320px;
  object-fit: contain;
  margin: 8px;
  cursor: zoom-in;
  /* Checkerboard so transparent PNGs stay readable on the dark theme */
  background: repeating-conic-gradient(#2d2d30 0% 25%, #252526 0% 50%) 0 0 / 16px 16px;
}
.media-image-zoomed {
  max-height: none;
  cursor: zoom-out;
}

.media-uri {
  padding: 6px 8px;
  color: #ce9178;
  font-family: "Cascadia Code", "Consolas", monospace;
  font-size: 11px;
  word-break: break-all;
}
</style>
