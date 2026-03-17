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
  method: string;
  urlPattern: string;
  bodyPattern: string;
  bodyPatternIsRegex: boolean;
  enabled: boolean;
  statusCode: number;
  contentType: string;
  headers: Record<string, string>;
  body: string;
}

export interface Recording {
  id: string;
  name: string;
  createdAt: string;
  active: boolean;
  rulesCount: number;
}

export interface RecordingStatus {
  recordingId: string | null;
  activeId: string | null;
}
