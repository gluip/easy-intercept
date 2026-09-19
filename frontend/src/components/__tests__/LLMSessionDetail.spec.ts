import { describe, it, expect } from "vitest";
import { createSSRApp, h } from "vue";
import { renderToString } from "@vue/server-renderer";
import LLMSessionDetail from "../LLMSessionDetail.vue";
import { loadSession } from "../../utils/__tests__/helpers";
import type { ProxySession } from "../../types";

function render(session: ProxySession): Promise<string> {
  return renderToString(createSSRApp({ render: () => h(LLMSessionDetail, { session }) }));
}

/** The image fixture with its response body replaced. */
function withResponse(body: unknown): ProxySession {
  return { ...loadSession("gemini-image-generate.json"), responseBody: JSON.stringify(body) };
}

function imgTags(html: string): string[] {
  return html.match(/<img[^>]*>/g) ?? [];
}

describe("LLMSessionDetail with a Gemini image response", () => {
  it("renders like any other LLM call: model, tokens, text", async () => {
    const html = await render(loadSession("gemini-image-generate.json"));
    expect(html).not.toContain("Could not parse as LLM response");
    expect(html).toContain("gemini-3.1-flash-image");
    expect(html).toContain("1,302"); // response tokens
    expect(html).toContain("Here is the gradient version of your image.");
    expect(html).toContain("Turn this checkerboard into a smooth colour gradient.");
  });

  it("prices the image at the image rate and shows the image token share", async () => {
    const html = await render(loadSession("gemini-image-generate.json"));
    expect(html).toContain("$0.078"); // $0.0041 at the text rate
    expect(html).toMatch(/pill-image[^>]*>\s*🖼 1,290/);
  });

  it("shows the generated image and the request's input image", async () => {
    const session = loadSession("gemini-image-generate.json");
    const generated = JSON.parse(session.responseBody).candidates[0].content.parts[1].inlineData.data;
    const input = JSON.parse(session.requestBody).contents[0].parts[1].inline_data.data;

    const imgs = imgTags(await render(session));
    expect(imgs).toHaveLength(2);
    // The response turn is drawn first
    expect(imgs[0]).toContain(`src="data:image/png;base64,${generated}"`);
    expect(imgs[1]).toContain(`src="data:image/png;base64,${input}"`);
  });

  it("labels each image with its type and size, and offers a download", async () => {
    const html = await render(loadSession("gemini-image-generate.json"));
    expect(html).toContain("image/png");
    expect(html).toMatch(/\d+(\.\d)? KB/);
    expect(html).toContain('download="image-2.png"');
  });

  it("does not dump the base64 into the page as text", async () => {
    const session = loadSession("gemini-image-generate.json");
    const data: string = JSON.parse(session.responseBody).candidates[0].content.parts[1].inlineData.data;
    const html = await render(session);
    // Once in the <img>, once in the download link, and nowhere else
    expect(html.split(data).length - 1).toBe(2);
  });

  it("shows an answer that is an image and nothing else", async () => {
    const data = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==";
    const html = await render(
      withResponse({
        candidates: [{ content: { role: "model", parts: [{ inlineData: { mimeType: "image/png", data } }] }, finishReason: "STOP" }],
        modelVersion: "gemini-3.1-flash-image",
      }),
    );
    expect(imgTags(html)[0]).toContain(`src="data:image/png;base64,${data}"`);
  });

  it("keeps a user turn that only holds an image", async () => {
    const session = loadSession("gemini-image-generate.json");
    const req = JSON.parse(session.requestBody);
    req.contents[0].parts = [req.contents[0].parts[1]];
    const html = await render({ ...session, requestBody: JSON.stringify(req) });
    expect(imgTags(html)).toHaveLength(2);
    expect(html).toContain("label-user");
  });

  it("lists a referenced file without trying to load it", async () => {
    const uri = "https://generativelanguage.googleapis.com/v1beta/files/abc123";
    const html = await render(
      withResponse({
        candidates: [{ content: { role: "model", parts: [{ fileData: { mimeType: "image/jpeg", fileUri: uri } }] } }],
      }),
    );
    expect(html).toContain(uri);
    expect(imgTags(html)).toHaveLength(1); // only the request's input image
  });

  it("renders text-only Gemini answers exactly as before", async () => {
    const html = await render(
      withResponse({
        candidates: [{ content: { role: "model", parts: [{ text: "No image this time." }] }, finishReason: "STOP" }],
        usageMetadata: { promptTokenCount: 5, candidatesTokenCount: 7 },
        modelVersion: "gemini-3.1-flash",
      }),
    );
    expect(html).toContain("No image this time.");
    expect(html).not.toContain("pill-image");
    expect(imgTags(html)).toHaveLength(1);
  });
});
