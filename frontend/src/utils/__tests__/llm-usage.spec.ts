import { describe, it, expect } from "vitest";
import type { ProxySession } from "../../types";
import { loadSession } from "./helpers";
import { extractLLMUsage, geminiImageOutputTokens } from "../llm-usage";
import { calcCost, formatCost } from "../llm-cost";

function fakeSession(url: string, requestBody: unknown, responseBody: unknown): ProxySession {
  return {
    id: "00000000-0000-0000-0000-000000000000",
    timestamp: new Date().toISOString(),
    method: "POST",
    url,
    requestHeaders: {},
    requestBody: typeof requestBody === "string" ? requestBody : JSON.stringify(requestBody),
    responseStatus: 200,
    responseHeaders: {},
    responseBody: typeof responseBody === "string" ? responseBody : JSON.stringify(responseBody),
    durationMs: 100,
  };
}

describe("extractLLMUsage - gemini interactions", () => {
  it("reads tokens off the interactions usage block", () => {
    expect(extractLLMUsage(loadSession("interactions-6816.json"))).toEqual({
      model: "gemini-3.8-flash",
      promptTokens: 2051,
      responseTokens: 10,
      cachedTokens: 0,
      thoughtTokens: 47,
    });
  });

  it("returns null when the interaction response body isn't usable", () => {
    const s = fakeSession(
      "https://generativelanguage.googleapis.com/v1beta/interactions",
      { model: "gemini-3.8-flash", input: [{ type: "user_input", content: [{ type: "text", text: "hi" }] }] },
      "", // still in flight
    );
    expect(extractLLMUsage(s)).toBeNull();
  });

  it("feeds a cost through the gemini pricing table", () => {
    const usage = extractLLMUsage(loadSession("interactions-6816.json"))!;
    const cost = calcCost("gemini", usage.model, usage.promptTokens, usage.responseTokens, usage.cachedTokens, usage.thoughtTokens);
    // 2051 in @ $0.75/M + (10 + 47) out @ $3.75/M — thinking tokens bill as output
    expect(cost).not.toBeNull();
    expect(cost!.inputCost).toBeCloseTo(0.00153825, 8);
    expect(cost!.outputCost).toBeCloseTo(0.00021375, 8);
    expect(formatCost(cost!)).toBe("$0.0018");
  });
});

// Regression cover for the extraction moved out of SessionList/App.vue
describe("extractLLMUsage - existing providers", () => {
  it("reads classic gemini generateContent usageMetadata", () => {
    const s = fakeSession(
      "https://generativelanguage.googleapis.com/v1beta/models/gemini-3.5-flash:generateContent",
      { contents: [{ role: "user", parts: [{ text: "hi" }] }] },
      {
        modelVersion: "gemini-3.5-flash",
        candidates: [{ content: { role: "model", parts: [{ text: "hoi" }] }, finishReason: "STOP" }],
        usageMetadata: {
          promptTokenCount: 100, candidatesTokenCount: 20,
          cachedContentTokenCount: 40, thoughtsTokenCount: 5,
        },
      },
    );
    expect(extractLLMUsage(s)).toEqual({
      model: "gemini-3.5-flash",
      promptTokens: 100, responseTokens: 20, cachedTokens: 40, thoughtTokens: 5, imageTokens: 0,
    });
  });

  it("reads anthropic messages usage", () => {
    const s = fakeSession(
      "https://api.anthropic.com/v1/messages",
      { model: "claude-sonnet-4-5", messages: [{ role: "user", content: "hi" }] },
      {
        model: "claude-sonnet-4-5",
        content: [{ type: "text", text: "hoi" }],
        stop_reason: "end_turn",
        usage: { input_tokens: 70, output_tokens: 12, cache_read_input_tokens: 30 },
      },
    );
    expect(extractLLMUsage(s)).toEqual({
      model: "claude-sonnet-4-5",
      promptTokens: 70, responseTokens: 12, cachedTokens: 30, thoughtTokens: 0,
    });
  });

  it("reads openai chat/completions usage", () => {
    const s = fakeSession(
      "https://api.openai.com/v1/chat/completions",
      { model: "gpt-4.1", messages: [{ role: "user", content: "hi" }] },
      {
        model: "gpt-4.1",
        choices: [{ finish_reason: "stop", message: { role: "assistant", content: "hoi" } }],
        usage: { prompt_tokens: 90, completion_tokens: 15, prompt_tokens_details: { cached_tokens: 25 } },
      },
    );
    expect(extractLLMUsage(s)).toEqual({
      model: "gpt-4.1",
      promptTokens: 90, responseTokens: 15, cachedTokens: 25, thoughtTokens: 0,
    });
  });

  it("reads openai /v1/responses usage", () => {
    const s = fakeSession(
      "https://api.openai.com/v1/responses",
      { model: "gpt-5.4", input: [{ type: "message", role: "user", content: "hi" }] },
      {
        model: "gpt-5.4",
        status: "completed",
        output: [],
        usage: {
          input_tokens: 200, output_tokens: 40,
          input_tokens_details: { cached_tokens: 60 },
          output_tokens_details: { reasoning_tokens: 18 },
        },
      },
    );
    expect(extractLLMUsage(s)).toEqual({
      model: "gpt-5.4",
      promptTokens: 200, responseTokens: 40, cachedTokens: 60, thoughtTokens: 18,
    });
  });

  it("returns null for non-LLM traffic and unparseable bodies", () => {
    expect(extractLLMUsage(fakeSession("https://example.com/api", {}, {}))).toBeNull();
    expect(
      extractLLMUsage(fakeSession("https://api.anthropic.com/v1/messages", "{}", "<html>502</html>")),
    ).toBeNull();
  });
});

describe("Gemini image model cost", () => {
  it("reads the image share of the answer from candidatesTokensDetails", () => {
    const usage = extractLLMUsage(loadSession("gemini-image-generate.json"))!;
    expect(usage).toMatchObject({
      model: "gemini-3.1-flash-image",
      promptTokens: 271, responseTokens: 1302, imageTokens: 1290,
    });
  });

  it("sums IMAGE entries and ignores everything else", () => {
    expect(geminiImageOutputTokens({
      candidatesTokensDetails: [
        { modality: "IMAGE", tokenCount: 1120 },
        { modality: "TEXT", tokenCount: 40 },
        { modality: "IMAGE", tokenCount: 1120 },
      ],
    })).toBe(2240);
    expect(geminiImageOutputTokens({ candidatesTokensDetails: [{ modality: "IMAGE" }] })).toBe(0);
    expect(geminiImageOutputTokens({ candidatesTokenCount: 20 })).toBe(0);
    expect(geminiImageOutputTokens(undefined)).toBe(0);
  });

  it("bills image tokens at the image rate and the rest as text", () => {
    const u = extractLLMUsage(loadSession("gemini-image-generate.json"))!;
    const cost = calcCost("gemini", u.model, u.promptTokens, u.responseTokens, u.cachedTokens, u.thoughtTokens, u.imageTokens)!;
    // 271 in @ $0.50/M; 1290 image @ $60/M + 12 text @ $3/M
    expect(cost.inputCost).toBeCloseTo(0.0001355, 8);
    expect(cost.outputCost).toBeCloseTo(0.0774 + 0.000036, 8);
    expect(formatCost(cost)).toBe("$0.078");
  });

  it("prices a 1K image as published: $0.067 on 3.1 Flash Image, $0.134 on 3 Pro Image", () => {
    expect(calcCost("gemini", "gemini-3.1-flash-image", 0, 1120, 0, 0, 1120)!.total).toBeCloseTo(0.0672, 6);
    expect(calcCost("gemini", "gemini-3-pro-image", 0, 1120, 0, 0, 1120)!.total).toBeCloseTo(0.1344, 6);
    expect(calcCost("gemini", "gemini-3.1-flash-lite-image", 0, 1120, 0, 0, 1120)!.total).toBeCloseTo(0.0336, 6);
    expect(calcCost("gemini", "gemini-2.5-flash-image", 0, 1290, 0, 0, 1290)!.total).toBeCloseTo(0.0387, 6);
  });

  it("does not let the image models shadow their text siblings", () => {
    // 1M output tokens each, no images
    expect(calcCost("gemini", "gemini-3.1-flash", 0, 1_000_000, 0, 0)!.total).toBeCloseTo(3.0, 6);
    expect(calcCost("gemini", "gemini-3.1-flash-lite", 0, 1_000_000, 0, 0)!.total).toBeCloseTo(1.5, 6);
    expect(calcCost("gemini", "gemini-3.1-flash-image", 0, 1_000_000, 0, 0)!.total).toBeCloseTo(3.0, 6);
  });

  it("falls back to the text rate for image tokens on a model without an image rate", () => {
    expect(calcCost("gemini", "gemini-3.1-flash", 0, 1000, 0, 0, 1000)!.total).toBeCloseTo(0.003, 8);
  });

  it("never bills more image tokens than the answer has", () => {
    expect(calcCost("gemini", "gemini-3.1-flash-image", 0, 100, 0, 0, 5000)!.total).toBeCloseTo(0.006, 8);
  });
});
