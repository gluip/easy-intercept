// Token usage extraction, shared by the session list's cost column and the
// multi-select aggregate in App.vue. Both used to carry an identical copy of this chain.

import type { ProxySession } from "../types";
import { detectLLMProvider } from "./llm-detection";
import {
  isStreamingResponse,
  parseOpenAIStream,
  parseAnthropicStream,
  parseCopilotResponsesStream,
  isOpenAIResponsesRequest,
  parseOpenAIResponses,
} from "./llm-stream-parser";
import { isGeminiInteractionsRequest, parseGeminiInteractionsResponse } from "./gemini-interactions";

export interface LLMUsage {
  model: string;
  promptTokens: number;
  responseTokens: number;
  cachedTokens: number;
  thoughtTokens: number;
}

/** Extract model + token counts from an LLM session, or null if it isn't one / can't be parsed. */
export function extractLLMUsage(session: ProxySession): LLMUsage | null {
  const provider = detectLLMProvider(session);
  if (!provider) return null;

  try {
    if (provider === "copilot") {
      const p = parseCopilotResponsesStream(session.responseBody);
      return {
        model: p.model,
        promptTokens: p.promptTokens,
        responseTokens: p.responseTokens,
        cachedTokens: p.cachedTokens,
        thoughtTokens: 0,
      };
    }

    if (provider === "gemini" && isGeminiInteractionsRequest(session.requestBody)) {
      const r = parseGeminiInteractionsResponse(session.responseBody);
      if (!r) return null;
      return {
        model: r.model,
        promptTokens: r.promptTokens,
        responseTokens: r.responseTokens,
        cachedTokens: r.cachedTokens,
        thoughtTokens: r.thoughtTokens,
      };
    }

    if (provider === "openai" && isOpenAIResponsesRequest(session.requestBody)) {
      const r = parseOpenAIResponses(session.responseBody);
      return {
        model: r.model,
        promptTokens: r.promptTokens,
        responseTokens: r.responseTokens,
        cachedTokens: r.cachedTokens,
        thoughtTokens: r.thoughtTokens,
      };
    }

    let res: any;
    if (isStreamingResponse(session.responseBody)) {
      if (provider === "openai") res = parseOpenAIStream(session.responseBody);
      else if (provider === "anthropic") res = parseAnthropicStream(session.responseBody);
      else res = JSON.parse(session.responseBody);
    } else {
      res = JSON.parse(session.responseBody);
    }

    if (provider === "gemini") {
      const u = res.usageMetadata ?? {};
      return {
        model: res.modelVersion ?? "unknown",
        promptTokens: u.promptTokenCount ?? 0,
        responseTokens: u.candidatesTokenCount ?? 0,
        cachedTokens: u.cachedContentTokenCount ?? 0,
        thoughtTokens: u.thoughtsTokenCount ?? 0,
      };
    }

    if (provider === "anthropic") {
      const u = res.usage ?? {};
      const req = JSON.parse(session.requestBody);
      return {
        model: res.model ?? req.model ?? "unknown",
        promptTokens: u.input_tokens ?? 0,
        responseTokens: u.output_tokens ?? 0,
        cachedTokens: u.cache_read_input_tokens ?? 0,
        thoughtTokens: 0,
      };
    }

    // openai chat/completions
    const u = res.usage ?? {};
    const req = JSON.parse(session.requestBody);
    return {
      model: res.model ?? req.model ?? "unknown",
      promptTokens: u.prompt_tokens ?? 0,
      responseTokens: u.completion_tokens ?? 0,
      cachedTokens: (u.prompt_tokens_details ?? {}).cached_tokens ?? 0,
      thoughtTokens: 0,
    };
  } catch {
    return null;
  }
}
