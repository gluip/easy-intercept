<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch } from "vue";
import { toString as qrToSvg } from "qrcode";

const props = defineProps<{
  uiPort: number;
  proxyPort: number;
}>();

const emit = defineEmits<{
  close: [];
}>();

const addresses = ref<string[]>([]);
const selected = ref("");
const loading = ref(true);
const qrSvg = ref("");
const copied = ref(false);

// Straight to the certificate: iOS offers the profile download as soon as Safari opens it
const caUrl = computed(() =>
  selected.value ? `http://${selected.value}:${props.uiPort}/ca` : "",
);

watch(caUrl, async (url) => {
  const svg = url ? await qrToSvg(url, { type: "svg", margin: 2, width: 200 }) : "";
  if (url === caUrl.value) qrSvg.value = svg; // ignore a slower render for an address no longer selected
});

async function loadAddresses() {
  try {
    const r = await fetch("/api/lan-addresses");
    const found: string[] = (await r.json()).addresses ?? [];
    // Opened through a LAN address already? That one is known to be reachable, so offer it first
    const current = location.hostname;
    addresses.value = found.includes(current)
      ? [current, ...found.filter((a) => a !== current)]
      : found;
  } catch (e) {
    console.error("Failed to load network addresses:", e);
  } finally {
    loading.value = false;
  }
  selected.value = addresses.value[0] ?? "";
}

async function copyUrl() {
  try {
    await navigator.clipboard.writeText(caUrl.value);
    copied.value = true;
    setTimeout(() => (copied.value = false), 1500);
  } catch (e) {
    console.error("Clipboard write failed:", e);
  }
}

function onKeyDown(e: KeyboardEvent) {
  if (e.key === "Escape") emit("close");
}

onMounted(() => {
  document.addEventListener("keydown", onKeyDown);
  loadAddresses();
});
onBeforeUnmount(() => document.removeEventListener("keydown", onKeyDown));
</script>

<template>
  <div class="phone-backdrop" @click.self="emit('close')">
    <div class="phone-card" role="dialog" aria-labelledby="phone-setup-title">
      <div class="phone-header">
        <span id="phone-setup-title">Intercept HTTPS on an iPhone / iPad</span>
        <button class="close-btn" title="Close (Esc)" @click="emit('close')">✕</button>
      </div>

      <div class="phone-body">
        <div class="qr-col">
          <div v-if="loading" class="qr-placeholder">Looking up network addresses…</div>
          <div v-else-if="!addresses.length" class="qr-placeholder">
            No network address found. Connect this machine to Wi-Fi or Ethernet and reopen this
            dialog.
          </div>
          <template v-else>
            <!-- SVG produced by the qrcode library from our own URL -->
            <div class="qr" v-html="qrSvg" />
            <div class="qr-hint">Scan with the iPhone camera</div>
            <label v-if="addresses.length > 1" class="addr-pick">
              Address
              <select v-model="selected">
                <option v-for="a in addresses" :key="a" :value="a">{{ a }}</option>
              </select>
            </label>
            <div class="ca-url">
              <code>{{ caUrl }}</code>
              <button class="copy-btn" @click="copyUrl">{{ copied ? "copied" : "copy" }}</button>
            </div>
          </template>
        </div>

        <div class="steps-col">
          <ol class="steps">
            <li>
              <strong>Installed one before?</strong> Remove it first under
              <em>Settings → General → VPN &amp; Device Management</em>. A regenerated CA is a
              different certificate, and the old one no longer works.
            </li>
            <li>
              Scan the code and open the link in <strong>Safari</strong> (other browsers can't
              install profiles). Tap <em>Allow</em>.
            </li>
            <li>
              <em>Settings → General → VPN &amp; Device Management</em> → tap
              <em>EasyIntercept Root CA</em> → <em>Install</em>.
            </li>
            <li>
              <em>Settings → General → About → Certificate Trust Settings</em> → turn on
              <em>EasyIntercept Root CA</em>.
              <span class="note">Easy to miss — without it HTTPS still fails.</span>
            </li>
            <li>
              <em>Settings → Wi-Fi</em> → ⓘ next to your network → <em>Configure Proxy</em> →
              <em>Manual</em>: server <code>{{ selected || "…" }}</code>, port
              <code>{{ proxyPort }}</code>.
            </li>
          </ol>
          <p class="footnote">
            The phone must be on the same network as this machine. If the link won't load, check
            that the firewall lets EasyIntercept accept incoming connections. Apps that pin their
            certificates can't be intercepted. Done testing? Set the proxy back to <em>Off</em>,
            or the phone loses its connection when EasyIntercept isn't running.
          </p>
        </div>
      </div>
    </div>
  </div>
</template>

<style scoped>
.phone-backdrop {
  position: fixed;
  inset: 0;
  background: rgba(0, 0, 0, 0.72);
  z-index: 1000;
  display: flex;
  align-items: center;
  justify-content: center;
  padding: 16px;
}

.phone-card {
  background: #1e1e1e;
  border: 1px solid #3e3e42;
  border-radius: 6px;
  width: min(760px, 100%);
  max-height: 100%;
  display: flex;
  flex-direction: column;
  overflow: hidden;
}

.phone-header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  padding: 10px 14px;
  border-bottom: 1px solid #3e3e42;
  color: #d4d4d4;
  font-size: 13px;
  font-weight: 600;
}

.close-btn {
  background: none;
  border: none;
  color: #858585;
  font-size: 14px;
  cursor: pointer;
  padding: 2px 6px;
}
.close-btn:hover {
  color: #d4d4d4;
}

.phone-body {
  display: flex;
  gap: 24px;
  padding: 18px;
  overflow-y: auto;
  flex-wrap: wrap;
}

.qr-col {
  display: flex;
  flex-direction: column;
  align-items: center;
  gap: 10px;
  width: 232px;
  flex-shrink: 0;
}

.qr {
  width: 216px;
  height: 216px;
  padding: 8px;
  background: #fff; /* scanners need dark-on-light */
  border-radius: 4px;
}
.qr :deep(svg) {
  display: block;
  width: 100%;
  height: 100%;
}

.qr-placeholder {
  width: 216px;
  min-height: 216px;
  display: flex;
  align-items: center;
  justify-content: center;
  text-align: center;
  padding: 16px;
  border: 1px dashed #4e4e52;
  border-radius: 4px;
  color: #858585;
  font-size: 12px;
  line-height: 1.5;
}

.qr-hint {
  color: #858585;
  font-size: 11px;
}

.addr-pick {
  display: flex;
  align-items: center;
  gap: 6px;
  color: #858585;
  font-size: 11px;
}
.addr-pick select {
  background: #3c3c3c;
  color: #d4d4d4;
  border: 1px solid #555;
  border-radius: 3px;
  padding: 2px 4px;
  font-size: 11px;
}

.ca-url {
  display: flex;
  align-items: center;
  gap: 6px;
  max-width: 100%;
}
.ca-url code {
  color: #4fc1ff;
  font-size: 11px;
  overflow-wrap: anywhere;
}
.copy-btn {
  font-size: 11px;
  color: #858585;
  background: #3c3c3c;
  border: 1px solid #4e4e52;
  border-radius: 3px;
  padding: 1px 6px;
  cursor: pointer;
  flex-shrink: 0;
}
.copy-btn:hover {
  color: #d4d4d4;
}

.steps-col {
  flex: 1;
  min-width: 260px;
}

.steps {
  color: #d4d4d4;
  font-size: 12px;
  line-height: 1.55;
  padding-left: 18px;
  display: flex;
  flex-direction: column;
  gap: 10px;
}
.steps em {
  color: #dcdcaa;
  font-style: normal;
}
.steps code,
.footnote code {
  color: #4ec9b0;
  background: #2d2d30;
  padding: 0 4px;
  border-radius: 3px;
}
.steps .note {
  display: block;
  color: #ce9178;
  font-size: 11px;
}

.footnote {
  margin-top: 16px;
  color: #858585;
  font-size: 11px;
  line-height: 1.5;
}
.footnote em {
  font-style: normal;
  color: #d4d4d4;
}
</style>
