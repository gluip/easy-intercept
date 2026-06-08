export interface ProxySession {
  id: string;
  timestamp: string;
  method: string;
  url: string;
  requestHeaders: Record<string, string>;
  requestBody: string;
  responseStatus: number;
  responseHeaders: Record<string, string>;
  responseBody: string;
  durationMs: number;
}

export interface AutoResponderRule {
  id: string;
  name: string;
  isEnabled: boolean;
  method: string;
  url: string;
  responseStatus: number;
  responseHeaders: Record<string, string>;
  responseBody: string;
  latencyMs: number;
  bodyMatchType: "none" | "contains" | "regex";
  bodyMatch: string;
}
