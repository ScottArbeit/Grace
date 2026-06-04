import { GRACE_HEADER_NAMES } from "./headers.js";

export interface GraceLifecycleDiagnostics {
  readonly supportStatus?: string;
  readonly unsupportedAfter?: string;
  readonly minimumVersion?: string;
  readonly recommendedVersion?: string;
  readonly updateUrl?: string;
  readonly uploadSessionState?: string;
  readonly uploadSessionReason?: string;
}

export function parseLifecycleDiagnostics(headers: Headers): GraceLifecycleDiagnostics {
  return removeUndefinedValues({
    supportStatus: headers.get(GRACE_HEADER_NAMES.lifecycleStatus) ?? undefined,
    unsupportedAfter: headers.get(GRACE_HEADER_NAMES.lifecycleUnsupportedAfter) ?? undefined,
    minimumVersion: headers.get(GRACE_HEADER_NAMES.lifecycleMinimumVersion) ?? undefined,
    recommendedVersion: headers.get(GRACE_HEADER_NAMES.lifecycleRecommendedVersion) ?? undefined,
    updateUrl: headers.get(GRACE_HEADER_NAMES.lifecycleUpdateUrl) ?? undefined,
    uploadSessionState: headers.get(GRACE_HEADER_NAMES.uploadSessionLifecycleState) ?? undefined,
    uploadSessionReason: headers.get(GRACE_HEADER_NAMES.uploadSessionLifecycleReason) ?? undefined,
  });
}

function removeUndefinedValues<T extends Record<string, string | undefined>>(value: T): Partial<T> {
  return Object.fromEntries(Object.entries(value).filter((entry): entry is [string, string] => entry[1] !== undefined)) as Partial<T>;
}
