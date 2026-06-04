export const DEFAULT_GRACE_BASE_URL = "http://localhost:5000";
export const GRACE_CLIENT_TYPE = "TypeScriptNode";
export const GRACE_CLIENT_VERSION = "0.0.0-s11";

export const GRACE_HEADER_NAMES = {
  apiVersion: "X-Api-Version",
  clientType: "X-Grace-Client-Type",
  clientVersion: "X-Grace-Client-Version",
  correlationId: "X-Correlation-Id",
  lifecycleStatus: "X-Grace-Client-Support-Status",
  lifecycleUnsupportedAfter: "X-Grace-Client-Unsupported-After",
  lifecycleMinimumVersion: "X-Grace-Client-Min-Version",
  lifecycleRecommendedVersion: "X-Grace-Client-Recommended-Version",
  lifecycleUpdateUrl: "X-Grace-Client-Update-Url",
  uploadSessionLifecycleState: "UploadSessionLifecycleState",
  uploadSessionLifecycleReason: "UploadSessionLifecycleReason",
} as const;

export function normalizeBaseUrl(value: string | URL): string {
  const url = value instanceof URL ? value : new URL(value);
  return url.toString().replace(/\/+$/, "");
}
