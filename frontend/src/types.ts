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

export interface AnalysisRun {
  id: string;
  name: string;
  hostFilter: string;
  createdAt: string;
  stoppedAt: string | null;
  eventCount: number;
}

export interface AnalysisStatus {
  runId: string | null;
}

export interface AnalysisEventSummary {
  sequence: number;
  fileName: string;
  timestamp: string;
  method: string;
  url: string;
  host: string;
  responseStatus: number;
  durationMs: number;
}

export interface AnalysisEvent {
  sequence: number;
  fileName: string;
  timestamp: string;
  method: string;
  url: string;
  host: string;
  durationMs: number;
  requestHeaders: Record<string, string>;
  requestContentType: string;
  requestBodyByteLength: number;
  requestBody: string;
  requestBodySkippedReason: string | null;
  responseStatus: number;
  responseHeaders: Record<string, string>;
  responseContentType: string;
  responseBodyByteLength: number;
  responseBody: string;
  responseBodySkippedReason: string | null;
}
