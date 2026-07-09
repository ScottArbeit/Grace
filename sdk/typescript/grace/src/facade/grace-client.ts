import { rawClientMetadata } from "../internal/generated/grace-raw-client-metadata.js";
import { GraceRawClient } from "../internal/generated/grace-raw-client.js";
import { createHash } from "node:crypto";
import { stat, readFile, writeFile } from "node:fs/promises";
import { dirname } from "node:path";
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

export interface GraceStorageScope {
  readonly ownerId?: string;
  readonly ownerName?: string;
  readonly organizationId?: string;
  readonly organizationName?: string;
  readonly repositoryId?: string;
  readonly repositoryName?: string;
  readonly principal?: string;
}

export interface GraceFileVersion {
  readonly Class?: "FileVersion" | string;
  readonly RelativePath: string;
  readonly Sha256Hash: string;
  readonly Blake3Hash?: string;
  readonly IsBinary?: boolean;
  readonly Size?: number;
  readonly CreatedAt?: string;
  readonly BlobUri?: string;
  readonly ContentReference?: GraceFileContentReference;
}

export interface GraceFileContentReference {
  readonly Class?: "FileContentReference" | string;
  readonly ReferenceType: "WholeFileContent" | "FileManifest" | string;
  readonly Manifest?: unknown;
}

export interface GraceUploadFileRequest extends GraceStorageScope {
  readonly filePath: string;
  readonly relativePath: string;
  readonly correlationId?: string;
  readonly contentType?: string;
  readonly onProgress?: GraceTransferProgressHandler;
  readonly signal?: AbortSignal;
}

export interface GraceDownloadFileRequest extends GraceStorageScope {
  readonly referenceId: string;
  readonly fileVersion: GraceFileVersion;
  readonly outputPath: string;
  readonly correlationId?: string;
  readonly allowOverwrite?: boolean;
  readonly onProgress?: GraceTransferProgressHandler;
  readonly signal?: AbortSignal;
}

export interface GraceTransferStep {
  readonly status: number;
  readonly headers: Headers;
  readonly lifecycle: GraceLifecycleDiagnostics;
}

export type GraceTransferOperation = "upload" | "download";

export type GraceTransferProgressStage = "api-requested" | "transfer-started" | "transfer-completed";

export interface GraceTransferProgressEvent {
  readonly operation: GraceTransferOperation;
  readonly stage: GraceTransferProgressStage;
  readonly relativePath: string;
  readonly bytesTransferred: number;
  readonly totalBytes?: number;
  readonly status?: number;
}

export type GraceTransferProgressHandler = (event: GraceTransferProgressEvent) => void | Promise<void>;

export interface GraceUploadFileResult {
  readonly fileVersion: GraceFileVersion;
  readonly relativePath: string;
  readonly sha256Hash: string;
  readonly size: number;
  readonly uriRequest: GraceClientResponse<Record<string, string>>;
  readonly transfer: GraceTransferStep;
}

export interface GraceDownloadFileResult {
  readonly fileVersion: GraceFileVersion;
  readonly outputPath: string;
  readonly bytesWritten: number;
  readonly uriRequest: GraceClientResponse<string>;
  readonly transfer: GraceTransferStep;
}

export class GraceClient {
  public readonly contract: GraceClientContractMetadata;
  public readonly baseUrl: string;
  public readonly apiVersion: string;

  private readonly auth?: GraceClientAuthProvider;
  private readonly correlationId?: string | (() => string | Promise<string>);
  private readonly defaultHeaders: Headers;
  private readonly rawClient: GraceRawClient;
  private readonly transferFetch: typeof fetch;

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
    this.transferFetch = options.fetch ?? globalThis.fetch;
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

  public async uploadFile(request: GraceUploadFileRequest): Promise<GraceUploadFileResult> {
    const fileVersion = await createWholeFileVersion(request.filePath, request.relativePath);
    const correlationId = request.correlationId ?? (await resolveValue(this.correlationId));
    const uriRequest = await this.request<Record<string, string>>({
      body: createStorageParameters({ ...request, correlationId }, {
        FileVersions: [fileVersion],
      }),
      correlationId,
      path: "/storage/getUploadUri",
      signal: request.signal,
    });

    await emitProgress(request.onProgress, {
      bytesTransferred: 0,
      operation: "upload",
      relativePath: fileVersion.RelativePath,
      stage: "api-requested",
      totalBytes: fileVersion.Size,
    });

    const uploadUri = readUploadUri(uriRequest.body, request.relativePath);
    const fileBytes = await readFile(request.filePath);
    await emitProgress(request.onProgress, {
      bytesTransferred: 0,
      operation: "upload",
      relativePath: fileVersion.RelativePath,
      stage: "transfer-started",
      totalBytes: fileBytes.byteLength,
    });

    const response = await this.transferFetch(uploadUri, {
      body: new Blob([fileBytes]),
      headers: createUploadHeaders(request.contentType),
      method: "PUT",
      signal: request.signal,
    });

    const transfer = await readTransferStep(response);

    if (!response.ok) {
      throw GraceError.fromResponse(response, transfer.body, transfer.lifecycle);
    }

    await emitProgress(request.onProgress, {
      bytesTransferred: fileBytes.byteLength,
      operation: "upload",
      relativePath: fileVersion.RelativePath,
      stage: "transfer-completed",
      status: transfer.status,
      totalBytes: fileBytes.byteLength,
    });

    return {
      fileVersion,
      relativePath: fileVersion.RelativePath,
      sha256Hash: fileVersion.Sha256Hash,
      size: fileVersion.Size ?? fileBytes.byteLength,
      uriRequest,
      transfer,
    };
  }

  public async downloadFile(request: GraceDownloadFileRequest): Promise<GraceDownloadFileResult> {
    await assertOutputPathIsWritable(request.outputPath, request.allowOverwrite ?? false);
    const correlationId = request.correlationId ?? (await resolveValue(this.correlationId));
    const normalizedFileVersion = normalizeDownloadFileVersion(request.fileVersion);

    const uriRequest = await this.request<string>({
      body: createStorageParameters({ ...request, correlationId }, {
        Blake3Hash: normalizedFileVersion.Blake3Hash,
        ReferenceId: normalizeRequiredString(request.referenceId, "referenceId"),
        RelativePath: normalizedFileVersion.RelativePath,
        Sha256Hash: normalizedFileVersion.Sha256Hash,
      }),
      correlationId,
      headers: {
        Accept: "text/plain",
      },
      path: "/storage/getDownloadUri",
      signal: request.signal,
    });

    if (typeof uriRequest.body !== "string" || uriRequest.body.trim() === "") {
      throw new Error("Grace did not return a download URI.");
    }

    await emitProgress(request.onProgress, {
      bytesTransferred: 0,
      operation: "download",
      relativePath: normalizedFileVersion.RelativePath,
      stage: "api-requested",
      totalBytes: normalizedFileVersion.Size,
    });

    await emitProgress(request.onProgress, {
      bytesTransferred: 0,
      operation: "download",
      relativePath: normalizedFileVersion.RelativePath,
      stage: "transfer-started",
      totalBytes: normalizedFileVersion.Size,
    });

    const response = await this.transferFetch(uriRequest.body, {
      method: "GET",
      signal: request.signal,
    });

    const transfer = await readTransferStep(response);

    if (!response.ok) {
      throw GraceError.fromResponse(response, transfer.body, transfer.lifecycle);
    }

    const bytes = transfer.bytes ?? new Uint8Array();
    await writeFile(request.outputPath, bytes);
    await emitProgress(request.onProgress, {
      bytesTransferred: bytes.byteLength,
      operation: "download",
      relativePath: normalizedFileVersion.RelativePath,
      stage: "transfer-completed",
      status: transfer.status,
      totalBytes: normalizedFileVersion.Size ?? bytes.byteLength,
    });

    return {
      bytesWritten: bytes.byteLength,
      fileVersion: normalizedFileVersion,
      outputPath: request.outputPath,
      uriRequest,
      transfer,
    };
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

async function createWholeFileVersion(filePath: string, relativePath: string): Promise<GraceFileVersion> {
  let fileInfo;

  try {
    fileInfo = await stat(filePath);
  } catch {
    throw new Error("Input file does not exist.");
  }

  if (!fileInfo.isFile()) {
    throw new Error("Input path must be a file.");
  }

  if (!relativePath || relativePath.trim() === "") {
    throw new Error("Relative path is required.");
  }

  const fileBytes = await readFile(filePath);
  const sha256Hash = createHash("sha256").update(fileBytes).digest("hex");

  return {
    BlobUri: "",
    Class: "FileVersion",
    ContentReference: {
      Class: "FileContentReference",
      Manifest: null,
      ReferenceType: "WholeFileContent",
    },
    CreatedAt: fileInfo.mtime.toISOString(),
    IsBinary: true,
    RelativePath: normalizeRelativePath(relativePath),
    Sha256Hash: sha256Hash,
    Size: fileInfo.size,
  };
}

function normalizeFileVersion(fileVersion: GraceFileVersion): GraceFileVersion {
  if (!fileVersion.RelativePath || fileVersion.RelativePath.trim() === "") {
    throw new Error("FileVersion.RelativePath is required.");
  }

  if (!fileVersion.Sha256Hash || fileVersion.Sha256Hash.trim() === "") {
    throw new Error("FileVersion.Sha256Hash is required.");
  }

  return {
    BlobUri: fileVersion.BlobUri ?? "",
    Class: fileVersion.Class ?? "FileVersion",
    ContentReference: fileVersion.ContentReference ?? {
      Class: "FileContentReference",
      Manifest: null,
      ReferenceType: "WholeFileContent",
    },
    CreatedAt: fileVersion.CreatedAt,
    IsBinary: fileVersion.IsBinary ?? true,
    Blake3Hash: fileVersion.Blake3Hash,
    RelativePath: normalizeRelativePath(fileVersion.RelativePath),
    Sha256Hash: fileVersion.Sha256Hash,
    Size: fileVersion.Size,
  };
}

function normalizeDownloadFileVersion(fileVersion: GraceFileVersion): GraceFileVersion {
  const normalizedFileVersion = normalizeFileVersion(fileVersion);

  if (normalizedFileVersion.ContentReference?.ReferenceType === "FileManifest") {
    throw new Error("Manifest-backed downloads require ContentBlock request fields and are not supported by this facade yet.");
  }

  return normalizedFileVersion;
}

function createStorageParameters(scope: GraceStorageScope & { readonly correlationId?: string }, body: Record<string, unknown>): Record<string, unknown> {
  return removeUndefined({
    CorrelationId: scope.correlationId,
    OwnerId: scope.ownerId,
    OwnerName: scope.ownerName,
    OrganizationId: scope.organizationId,
    OrganizationName: scope.organizationName,
    Principal: scope.principal,
    RepositoryId: scope.repositoryId,
    RepositoryName: scope.repositoryName,
    ...body,
  });
}

function readUploadUri(uriMap: Record<string, string>, relativePath: string): string {
  const normalizedRelativePath = normalizeRelativePath(relativePath);
  const uploadUri = uriMap[normalizedRelativePath] ?? uriMap[`/${normalizedRelativePath}`];

  if (!uploadUri || uploadUri.trim() === "") {
    throw new Error("Grace did not return an upload URI for the requested file.");
  }

  return uploadUri;
}

function createUploadHeaders(contentType?: string): Headers {
  const headers = new Headers();
  headers.set("Content-Type", contentType ?? "application/octet-stream");
  headers.set("x-ms-blob-type", "BlockBlob");
  return headers;
}

async function readTransferStep(response: Response): Promise<GraceTransferStep & { readonly body?: unknown; readonly bytes?: Uint8Array }> {
  const lifecycle = parseLifecycleDiagnostics(response.headers);
  const contentType = response.headers.get("Content-Type") ?? "";

  if (!response.ok) {
    const text = await response.text();
    const body = contentType.toLowerCase().includes("application/json") && text ? JSON.parse(text) : text;
    return {
      body,
      headers: response.headers,
      lifecycle,
      status: response.status,
    };
  }

  if (response.status === 204 || response.status === 201) {
    return {
      headers: response.headers,
      lifecycle,
      status: response.status,
    };
  }

  return {
    bytes: new Uint8Array(await response.arrayBuffer()),
    headers: response.headers,
    lifecycle,
    status: response.status,
  };
}

async function emitProgress(handler: GraceTransferProgressHandler | undefined, event: GraceTransferProgressEvent): Promise<void> {
  if (!handler) {
    return;
  }

  await handler(event);
}

async function assertOutputPathIsWritable(outputPath: string, allowOverwrite: boolean): Promise<void> {
  if (!outputPath || outputPath.trim() === "") {
    throw new Error("Output path is required.");
  }

  try {
    const parent = await stat(dirname(outputPath));
    if (!parent.isDirectory()) {
      throw new Error("Output directory does not exist.");
    }
  } catch {
    throw new Error("Output directory does not exist.");
  }

  try {
    const existingOutput = await stat(outputPath);
    if (existingOutput.isDirectory()) {
      throw new Error("Output path must be a file.");
    }

    if (!allowOverwrite) {
      throw new Error("Output file already exists.");
    }
  } catch (error) {
    if (error instanceof Error && error.message.startsWith("Output ")) {
      throw error;
    }
  }
}

function normalizeRelativePath(relativePath: string): string {
  return relativePath.replace(/\\/g, "/").replace(/^\/+/, "");
}

function normalizeRequiredString(value: string, fieldName: string): string {
  if (!value || value.trim() === "") {
    throw new Error(`${fieldName} is required.`);
  }

  return value.trim();
}

function removeUndefined(value: Record<string, unknown>): Record<string, unknown> {
  return Object.fromEntries(Object.entries(value).filter((entry) => entry[1] !== undefined));
}
