// Media carried inside LLM message parts.
//
// Gemini's generateContent puts binary payloads in the JSON itself: image models
// (gemini-*-image) answer with `parts[].inlineData { mimeType, data }` where `data`
// is base64, and image-editing requests send their input the same way. Larger
// inputs are passed by reference instead, as `fileData { mimeType, fileUri }`.
// The REST API uses camelCase; SDKs and hand-written requests also use snake_case.

export interface LLMMedia {
  mimeType: string;
  /** True when this can go straight into an <img>. */
  isImage: boolean;
  /** A data: URL for inline media, the file URI for referenced media. */
  src: string;
  /** Inline payloads only: `src` is a data: URL holding the bytes. */
  inline: boolean;
  /** Decoded size; 0 for referenced media. */
  bytes: number;
}

const BASE64_RE = /^[A-Za-z0-9+/]+={0,2}$/;
const MIME_RE = /^[a-z0-9][a-z0-9.+-]*\/[a-z0-9][a-z0-9.+-]*$/;

function asRecord(v: unknown): Record<string, unknown> | null {
  return v !== null && typeof v === "object" && !Array.isArray(v)
    ? (v as Record<string, unknown>)
    : null;
}

/** Lowercased type/subtype without parameters; a generic fallback if it isn't one. */
function cleanMimeType(v: unknown): string {
  const mime = typeof v === "string" ? v.split(";")[0].trim().toLowerCase() : "";
  return MIME_RE.test(mime) ? mime : "application/octet-stream";
}

/** Standard-alphabet base64 without whitespace, or null if `v` isn't base64. */
function cleanBase64(v: unknown): string | null {
  if (typeof v !== "string") return null;
  // Images run to megabytes, so only rewrite the string when it needs it
  if (BASE64_RE.test(v)) return v;
  const b64 = v.replace(/\s+/g, "").replace(/-/g, "+").replace(/_/g, "/");
  return BASE64_RE.test(b64) ? b64 : null;
}

export function base64ByteLength(b64: string): number {
  const padding = b64.endsWith("==") ? 2 : b64.endsWith("=") ? 1 : 0;
  return Math.max(0, Math.floor((b64.length * 3) / 4) - padding);
}

/** Data URL for a `{ mimeType, data }` pair if it holds a base64 image, else null. */
export function imageDataUrl(mimeType: unknown, data: unknown): string | null {
  const mime = cleanMimeType(mimeType);
  if (!mime.startsWith("image/")) return null;
  const b64 = cleanBase64(data);
  return b64 ? `data:${mime};base64,${b64}` : null;
}

/** The media a message part carries, or null for text / tool parts. */
export function partMedia(part: unknown): LLMMedia | null {
  const p = asRecord(part);
  if (!p) return null;

  const inlineData = asRecord(p.inlineData ?? p.inline_data);
  if (inlineData) {
    const b64 = cleanBase64(inlineData.data);
    if (!b64) return null;
    const mimeType = cleanMimeType(inlineData.mimeType ?? inlineData.mime_type);
    return {
      mimeType,
      isImage: mimeType.startsWith("image/"),
      src: `data:${mimeType};base64,${b64}`,
      inline: true,
      bytes: base64ByteLength(b64),
    };
  }

  const fileData = asRecord(p.fileData ?? p.file_data);
  if (fileData) {
    const uri = fileData.fileUri ?? fileData.file_uri;
    if (typeof uri !== "string" || !uri) return null;
    // Referenced files need the caller's credentials to fetch, so never an <img>.
    return {
      mimeType: cleanMimeType(fileData.mimeType ?? fileData.mime_type),
      isImage: false,
      src: uri,
      inline: false,
      bytes: 0,
    };
  }

  return null;
}

export function formatBytes(bytes: number): string {
  if (bytes < 1024) return `${bytes} B`;
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`;
  return `${(bytes / (1024 * 1024)).toFixed(1)} MB`;
}

/** File name for the download link, e.g. "image-2.png". */
export function mediaFileName(media: LLMMedia, index: number): string {
  const [type, subtype] = media.mimeType.split("/");
  const ext = subtype === "jpeg" ? "jpg" : subtype.split("+")[0].replace(/^x-/, "");
  const stem = type === "application" ? "file" : type;
  return `${stem}-${index + 1}.${ext === "octet-stream" ? "bin" : ext}`;
}

/** One-line summary of a part list's media for the session list, e.g. "🖼 2 images". */
export function mediaPreviewText(parts: unknown): string | null {
  if (!Array.isArray(parts)) return null;
  const media = parts.map(partMedia).filter((m): m is LLMMedia => m !== null);
  if (media.length === 0) return null;
  const images = media.filter((m) => m.isImage);
  if (images.length === media.length) {
    return images.length === 1 ? `🖼 ${images[0].mimeType}` : `🖼 ${images.length} images`;
  }
  return media.length === 1 ? `📎 ${media[0].mimeType}` : `📎 ${media.length} files`;
}
