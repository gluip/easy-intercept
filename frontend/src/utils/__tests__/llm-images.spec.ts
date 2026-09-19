import { describe, it, expect } from "vitest";
import { loadSession } from "./helpers";
import {
  partMedia,
  imageDataUrl,
  base64ByteLength,
  formatBytes,
  mediaFileName,
  mediaPreviewText,
} from "../llm-images";

// generateContent on an image model: the request carries a snake_case input image,
// the response a text part plus a camelCase inlineData PNG.
const IMAGE_SESSION = "gemini-image-generate.json";

const PNG_MAGIC = "iVBORw0KGgo"; // base64 of the 8-byte PNG signature

function responseParts(): unknown[] {
  const res = JSON.parse(loadSession(IMAGE_SESSION).responseBody);
  return res.candidates[0].content.parts;
}

function requestParts(): unknown[] {
  const req = JSON.parse(loadSession(IMAGE_SESSION).requestBody);
  return req.contents[0].parts;
}

describe("partMedia", () => {
  it("turns a response inlineData part into an image data URL", () => {
    const media = partMedia(responseParts()[1]);
    expect(media).not.toBeNull();
    expect(media!.isImage).toBe(true);
    expect(media!.inline).toBe(true);
    expect(media!.mimeType).toBe("image/png");
    expect(media!.src.startsWith(`data:image/png;base64,${PNG_MAGIC}`)).toBe(true);
  });

  it("reads the snake_case spelling used in requests", () => {
    const media = partMedia(requestParts()[1]);
    expect(media?.isImage).toBe(true);
    expect(media?.src.startsWith(`data:image/png;base64,${PNG_MAGIC}`)).toBe(true);
  });

  it("reports the decoded size, not the base64 length", () => {
    const part = requestParts()[1] as { inline_data: { data: string } };
    const decoded = Buffer.from(part.inline_data.data, "base64").length;
    expect(partMedia(part)!.bytes).toBe(decoded);
  });

  it("returns null for text and tool parts", () => {
    expect(partMedia(responseParts()[0])).toBeNull();
    expect(partMedia({ functionCall: { name: "f", args: {} } })).toBeNull();
    expect(partMedia(null)).toBeNull();
    expect(partMedia("inlineData")).toBeNull();
  });

  it("keeps non-image inline data as a downloadable file, not an image", () => {
    const media = partMedia({ inlineData: { mimeType: "audio/mp3", data: "AAAA" } });
    expect(media).toMatchObject({ isImage: false, inline: true, mimeType: "audio/mp3", bytes: 3 });
  });

  it("drops content-type parameters and odd casing from the data URL", () => {
    const media = partMedia({ inlineData: { mimeType: "Image/PNG; charset=binary", data: "AAAA" } });
    expect(media?.src).toBe("data:image/png;base64,AAAA");
  });

  it("never lets a malformed mime type into the data URL", () => {
    const media = partMedia({ inlineData: { mimeType: 'image/png" onerror="x', data: "AAAA" } });
    expect(media).toMatchObject({ mimeType: "application/octet-stream", isImage: false });
  });

  it("normalises url-safe and line-wrapped base64", () => {
    expect(partMedia({ inlineData: { mimeType: "image/png", data: "-_8A\n-_8A" } })?.src).toBe(
      "data:image/png;base64,+/8A+/8A",
    );
  });

  it("rejects data that is missing or not base64", () => {
    expect(partMedia({ inlineData: { mimeType: "image/png" } })).toBeNull();
    expect(partMedia({ inlineData: { mimeType: "image/png", data: "" } })).toBeNull();
    expect(partMedia({ inlineData: { mimeType: "image/png", data: "<svg onload=x>" } })).toBeNull();
    expect(partMedia({ inlineData: { mimeType: "image/png", data: 42 } })).toBeNull();
  });

  it("shows referenced files as a link, never as an image", () => {
    const uri = "https://generativelanguage.googleapis.com/v1beta/files/abc123";
    expect(partMedia({ fileData: { mimeType: "image/jpeg", fileUri: uri } })).toEqual({
      mimeType: "image/jpeg",
      isImage: false,
      src: uri,
      inline: false,
      bytes: 0,
    });
    expect(partMedia({ file_data: { mime_type: "video/mp4", file_uri: uri } })?.src).toBe(uri);
    expect(partMedia({ fileData: { mimeType: "image/jpeg" } })).toBeNull();
  });
});

describe("imageDataUrl", () => {
  it("builds a URL for image pairs only", () => {
    expect(imageDataUrl("image/webp", "AAAA")).toBe("data:image/webp;base64,AAAA");
    expect(imageDataUrl("application/pdf", "AAAA")).toBeNull();
    expect(imageDataUrl(undefined, "AAAA")).toBeNull();
    expect(imageDataUrl("image/png", { not: "a string" })).toBeNull();
  });
});

describe("base64ByteLength", () => {
  it("accounts for padding", () => {
    expect(base64ByteLength("AAAA")).toBe(3);
    expect(base64ByteLength("AAA=")).toBe(2);
    expect(base64ByteLength("AA==")).toBe(1);
    expect(base64ByteLength("")).toBe(0);
  });
});

describe("formatBytes", () => {
  it("picks a readable unit", () => {
    expect(formatBytes(512)).toBe("512 B");
    expect(formatBytes(2048)).toBe("2.0 KB");
    expect(formatBytes(1.5 * 1024 * 1024)).toBe("1.5 MB");
  });
});

describe("mediaFileName", () => {
  const media = (mimeType: string) => partMedia({ inlineData: { mimeType, data: "AAAA" } })!;

  it("derives the extension from the mime type", () => {
    expect(mediaFileName(media("image/png"), 0)).toBe("image-1.png");
    expect(mediaFileName(media("image/jpeg"), 1)).toBe("image-2.jpg");
    expect(mediaFileName(media("image/svg+xml"), 0)).toBe("image-1.svg");
    expect(mediaFileName(media("application/pdf"), 2)).toBe("file-3.pdf");
    expect(mediaFileName(media("nonsense"), 0)).toBe("file-1.bin");
  });
});

describe("mediaPreviewText", () => {
  it("names the image type of an image answer", () => {
    expect(mediaPreviewText(responseParts())).toBe("🖼 image/png");
  });

  it("counts several images", () => {
    const img = responseParts()[1];
    expect(mediaPreviewText([img, img, img])).toBe("🖼 3 images");
  });

  it("falls back to a file label for mixed or non-image media", () => {
    const pdf = { inlineData: { mimeType: "application/pdf", data: "AAAA" } };
    expect(mediaPreviewText([pdf])).toBe("📎 application/pdf");
    expect(mediaPreviewText([pdf, responseParts()[1]])).toBe("📎 2 files");
  });

  it("is null when there is no media", () => {
    expect(mediaPreviewText([{ text: "hi" }])).toBeNull();
    expect(mediaPreviewText(undefined)).toBeNull();
  });
});
