import { rawClientMetadata } from "../internal/generated/grace-raw-client-metadata.js";
import { GraceRawClient } from "../internal/generated/grace-raw-client.js";
import { GraceError } from "./grace-error.js";
import {
  DEFAULT_GRACE_BASE_URL,
  GRACE_CLIENT_TYPE,
  GRACE_CLIENT_VERSION,
  GRACE_HEADER_NAMES,
  normalizeBaseUrl,
} from "./headers.js";
import { parseLifecycleDiagnostics, type GraceLifecycleDiagnostics } from "./lifecycle.js";

export interface GraceClientContractMetadata {
  readonly apiContractVersion: string;
  readonly openApiProjectionSha256: string;
}

export type GraceClientAuthProvider = string | (() => string | Promise<string>);

export interface GraceClientOptions {
  readonly baseUrl?: string | URL;
  readonly apiVersion?: string;
  readonly auth?: GraceClientAuthProvider;
  readonly correlationId?: string | (() => string | Promise<string>);
  readonly fetch?: typeof fetch;
  readonly headers?: HeadersInit;
}

export interface GraceClientRequest {
  readonly method?: string;
  readonly path: string;
  readonly query?: URLSearchParams | Record<string, string | number | boolean | undefined>;
  readonly body?: unknown;
  readonly correlationId?: string;
  readonly headers?: HeadersInit;
  readonly signal?: AbortSignal;
}

export interface GraceClientResponse<TBody = unknown> {
  readonly body: TBody;
  readonly status: number;
  readonly headers: Headers;
  readonly correlationId?: string;
  readonly lifecycle: GraceLifecycleDiagnostics;
}

export class GraceClient {
  public readonly contract: GraceClientContractMetadata;
  public readonly baseUrl: string;
  public readonly apiVersion: string;

  private readonly auth?: GraceClientAuthProvider;
  private readonly correlationId?: string | (() => string | Promise<string>);
  private readonly defaultHeaders: Headers;
  private readonly rawClient: GraceRawClient;

  public constructor(options: GraceClientOptions = {}) {
    this.contract = {
      apiContractVersion: rawClientMetadata.apiContractVersion,
      openApiProjectionSha256: rawClientMetadata.openApiProjectionSha256,
    };

    this.baseUrl = normalizeBaseUrl(options.baseUrl ?? DEFAULT_GRACE_BASE_URL);
    this.apiVersion = options.apiVersion ?? rawClientMetadata.apiContractVersion;
    this.auth = options.auth;
    this.correlationId = options.correlationId;
    this.defaultHeaders = new Headers(options.headers);
    this.rawClient = new GraceRawClient(options.fetch);
  }

  public async request<TBody = unknown>(request: GraceClientRequest): Promise<GraceClientResponse<TBody>> {
    const url = this.createUrl(request.path, request.query);
    const headers = await this.createHeaders(request);
    const response = await this.rawClient.request({
      init: {
        body: serializeBody(request.body, headers),
        headers,
        method: request.method ?? (request.body === undefined ? "GET" : "POST"),
        signal: request.signal,
      },
      url,
    });

    const lifecycle = parseLifecycleDiagnostics(response.headers);
    const correlationId = response.headers.get(GRACE_HEADER_NAMES.correlationId) ?? undefined;

    if (!response.ok) {
      throw GraceError.fromResponse(response, response.body, lifecycle, correlationId);
    }

    return {
      body: response.body as TBody,
      status: response.status,
      headers: response.headers,
      correlationId,
      lifecycle,
    };
  }

  public unsupportedFileTransfer(): never {
    throw new Error("Tier 2 file transfer is not implemented by the TypeScript Node Tier 1 facade.");
  }

  private createUrl(path: string, query?: GraceClientRequest["query"]): URL {
    const normalizedPath = path.startsWith("/") ? path.slice(1) : path;
    const url = new URL(normalizedPath, `${this.baseUrl}/`);

    if (query instanceof URLSearchParams) {
      query.forEach((value, key) => url.searchParams.append(key, value));
    } else if (query) {
      for (const [key, value] of Object.entries(query)) {
        if (value !== undefined) {
          url.searchParams.append(key, String(value));
        }
      }
    }

    return url;
  }

  private async createHeaders(request: GraceClientRequest): Promise<Headers> {
    const headers = new Headers(this.defaultHeaders);
    mergeHeaders(headers, request.headers);

    if (!headers.has("Accept")) {
      headers.set("Accept", "application/json");
    }

    headers.set(GRACE_HEADER_NAMES.apiVersion, this.apiVersion);
    headers.set(GRACE_HEADER_NAMES.clientType, GRACE_CLIENT_TYPE);
    headers.set(GRACE_HEADER_NAMES.clientVersion, GRACE_CLIENT_VERSION);

    const correlationId = request.correlationId ?? (await resolveValue(this.correlationId));
    if (correlationId) {
      headers.set(GRACE_HEADER_NAMES.correlationId, correlationId);
    }

    const token = await resolveValue(this.auth);
    if (token && !headers.has("Authorization")) {
      headers.set("Authorization", token.startsWith("Bearer ") ? token : `Bearer ${token}`);
    }

    return headers;
  }
}

function mergeHeaders(target: Headers, source?: HeadersInit): void {
  if (!source) {
    return;
  }

  new Headers(source).forEach((value, key) => target.set(key, value));
}

async function resolveValue(value?: string | (() => string | Promise<string>)): Promise<string | undefined> {
  if (typeof value === "function") {
    return value();
  }

  return value;
}

function serializeBody(body: unknown, headers: Headers): BodyInit | undefined {
  if (body === undefined) {
    return undefined;
  }

  if (typeof body === "string" || body instanceof FormData || body instanceof Blob || body instanceof ArrayBuffer) {
    return body;
  }

  if (!headers.has("Content-Type")) {
    headers.set("Content-Type", "application/json");
  }

  return JSON.stringify(body);
}
