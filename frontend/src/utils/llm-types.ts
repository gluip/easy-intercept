// Shared internal shape that every LLM provider is normalized into.
// Modelled on Gemini's generateContent vocabulary (parts / functionCall / role "model").

export interface GPart {
  text?: string;
  functionCall?: { id?: string; name: string; args: Record<string, unknown> };
  functionResponse?: { name: string; response: unknown };
  thoughtSignature?: string;
  thinking?: string;
}

export interface GTurn {
  role: "user" | "model";
  parts: GPart[];
}

export interface ToolDef {
  name: string;
  description?: string;
  parameters?: unknown; // JSON schema
}

export interface ParsedLLM {
  provider: "gemini" | "anthropic" | "openai" | "copilot";
  modelVersion: string;
  system?: string;
  turns: GTurn[];
  responseTurn: GTurn | null;
  tools: ToolDef[];
  promptTokens: number;
  responseTokens: number;
  cachedTokens: number;
  thoughtTokens: number;
  finishReason: string;
  reasoningEffort?: string;
}
