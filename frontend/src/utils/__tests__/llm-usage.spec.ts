import { describe, it, expect } from "vitest";
import type { ProxySession } from "../../types";
import { loadSession } from "./helpers";
import { extractLLMUsage } from "../llm-usage";
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
      promptTokens: 100, responseTokens: 20, cachedTokens: 40, thoughtTokens: 5,
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
