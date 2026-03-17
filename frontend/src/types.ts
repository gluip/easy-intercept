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

export interface PinnedResponse {
  statusCode: number;
  headers: Record<string, string>;
  body: string;
}
