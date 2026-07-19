import type { ProxySession } from "../types";

export type LLMProvider = "gemini" | "anthropic" | "openai" | "copilot" | null;

export function detectLLMProvider(session: ProxySession): LLMProvider {
  const url = session.url.toLowerCase();
  if (url.includes("generativelanguage.googleapis.com")) return "gemini";
  if (url.includes("api.anthropic.com/v1/messages")) return "anthropic";
  if (url.includes("/v1/messages")) return "anthropic";
  if (url.includes("githubcopilot.com/responses")) return "copilot";
  if (url.includes("api.openai.com/v1/chat/completions")) return "openai";
  if (url.includes("api.openai.com/v1/responses")) return "openai";
  if (url.includes("/chat/completions")) return "openai";
  if (url.includes("/v1/responses")) return "openai";
  return null;
}

export function isLLMRequest(session: ProxySession): boolean {
  return detectLLMProvider(session) !== null;
}
