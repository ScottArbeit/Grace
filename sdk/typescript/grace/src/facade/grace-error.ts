import { type GraceLifecycleDiagnostics } from "./lifecycle.js";

export interface GraceErrorBody {
  readonly Error?: string;
  readonly EventTime?: string;
  readonly CorrelationId?: string;
  readonly Properties?: Record<string, unknown>;
}

interface GraceErrorResponse {
  readonly status: number;
  readonly statusText: string;
}

export class GraceError extends Error {
  public readonly status: number;
  public readonly body: unknown;
  public readonly correlationId?: string;
  public readonly lifecycle: GraceLifecycleDiagnostics;
  public readonly properties: Record<string, unknown>;
  public readonly unsupportedClient: boolean;

  private constructor(
    message: string,
    status: number,
    body: unknown,
    lifecycle: GraceLifecycleDiagnostics,
    correlationId?: string,
    properties: Record<string, unknown> = {},
  ) {
    super(message);
    this.name = "GraceError";
    this.status = status;
    this.body = body;
    this.correlationId = correlationId;
    this.lifecycle = lifecycle;
    this.properties = properties;
    this.unsupportedClient = message === "UnsupportedClientVersion" || lifecycle.supportStatus === "unsupported";
  }

  public static fromResponse(
    response: GraceErrorResponse,
    body: unknown,
    lifecycle: GraceLifecycleDiagnostics,
    correlationId?: string,
  ): GraceError {
    if (isGraceErrorBody(body)) {
      return new GraceError(
        body.Error ?? response.statusText,
        response.status,
        body,
        lifecycle,
        correlationId ?? body.CorrelationId,
        body.Properties ?? {},
      );
    }

    const message = typeof body === "string" && body ? body : response.statusText || `HTTP ${response.status}`;
    return new GraceError(message, response.status, body, lifecycle, correlationId);
  }
}

function isGraceErrorBody(body: unknown): body is GraceErrorBody {
  return typeof body === "object" && body !== null && "Error" in body;
}
