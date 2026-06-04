export interface GraceRawClientRequest {
  readonly url: URL;
  readonly init: RequestInit;
}

export interface GraceRawClientResponse {
  readonly body: unknown;
  readonly headers: Headers;
  readonly ok: boolean;
  readonly status: number;
  readonly statusText: string;
}

export class GraceRawClient {
  private readonly customFetch: typeof fetch;

  public constructor(customFetch: typeof fetch = globalThis.fetch) {
    if (!customFetch) {
      throw new TypeError("GraceClient requires a fetch implementation. Use Node 20+ or pass options.fetch.");
    }

    this.customFetch = customFetch;
  }

  public async request(request: GraceRawClientRequest): Promise<GraceRawClientResponse> {
    const response = await this.customFetch(request.url, request.init);

    return {
      body: await readResponseBody(response),
      headers: response.headers,
      ok: response.ok,
      status: response.status,
      statusText: response.statusText,
    };
  }
}

async function readResponseBody(response: Response): Promise<unknown> {
  if (response.status === 204) {
    return undefined;
  }

  const text = await response.text();
  if (!text) {
    return undefined;
  }

  const contentType = response.headers.get("Content-Type") ?? "";
  if (contentType.toLowerCase().includes("application/json")) {
    return JSON.parse(text);
  }

  return text;
}
