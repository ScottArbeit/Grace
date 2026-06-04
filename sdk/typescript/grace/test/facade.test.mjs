import assert from "node:assert/strict";
import { mkdtemp, readFile, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { test } from "node:test";
import {
  GraceClient,
  GraceError,
  GRACE_CLIENT_TYPE,
  GRACE_CLIENT_VERSION,
  GRACE_HEADER_NAMES,
} from "../dist/index.js";

test("public exports stay facade-first", async () => {
  const exports = Object.keys(await import("../dist/index.js")).sort();

  assert.deepEqual(exports, [
    "DEFAULT_GRACE_BASE_URL",
    "DEFAULT_MANIFEST_ELIGIBILITY_POLICY",
    "GRACE_CHUNKING_SUITE_ID",
    "GRACE_CLIENT_TYPE",
    "GRACE_CLIENT_VERSION",
    "GRACE_CONTENT_BLOCK_FORMAT",
    "GRACE_HEADER_NAMES",
    "GRACE_PROTOCOL_VECTOR_SUITE",
    "GRACE_PROTOCOL_VERSION",
    "GraceClient",
    "GraceContentBlockError",
    "GraceError",
    "GraceManifestValidationError",
    "assertCanonicalGraceAddress",
    "bytesToHex",
    "computeBlake3Bytes",
    "computeBlake3Hex",
    "computeChunkAddress",
    "computeContentBlockAddress",
    "computeManifestAddress",
    "contentBlockPreimage",
    "decideManifestEligibility",
    "decodeContentBlock",
    "detectBinaryByNulScan",
    "encodeContentBlock",
    "hexToBytes",
    "isCanonicalGraceAddress",
    "manifestPreimage",
    "validateFileManifest",
  ]);
});

test("default request policy sends pinned API version and TypeScript Node identity", async () => {
  const calls = [];
  const client = new GraceClient({
    fetch: async (url, init) => {
      calls.push({ init, url });
      return jsonResponse({ ReturnValue: "ok" }, { "X-Correlation-Id": "corr-response" });
    },
  });

  const response = await client.request({ path: "/owner/get", query: { ownerName: "scott", include: true } });

  assert.equal(client.contract.apiContractVersion, "2023-10-01");
  assert.notEqual(client.apiVersion, "edge");
  assert.equal(calls[0].url.toString(), "http://localhost:5000/owner/get?ownerName=scott&include=true");
  assert.equal(calls[0].init.headers.get(GRACE_HEADER_NAMES.apiVersion), "2023-10-01");
  assert.equal(calls[0].init.headers.get(GRACE_HEADER_NAMES.clientType), GRACE_CLIENT_TYPE);
  assert.equal(calls[0].init.headers.get(GRACE_HEADER_NAMES.clientVersion), GRACE_CLIENT_VERSION);
  assert.equal(response.correlationId, "corr-response");
});

test("explicit base URL, API version, bearer auth, and correlation ID are applied", async () => {
  const calls = [];
  const client = new GraceClient({
    apiVersion: "latest",
    auth: async () => "token-value",
    baseUrl: "https://grace.example.test/api/",
    correlationId: () => "corr-option",
    fetch: async (url, init) => {
      calls.push({ init, url });
      return jsonResponse({ ReturnValue: { id: 1 } });
    },
  });

  await client.request({ body: { Name: "sample" }, path: "repository/create", correlationId: "corr-request" });

  assert.equal(calls[0].url.toString(), "https://grace.example.test/api/repository/create");
  assert.equal(calls[0].init.method, "POST");
  assert.equal(calls[0].init.headers.get(GRACE_HEADER_NAMES.apiVersion), "latest");
  assert.equal(calls[0].init.headers.get("Authorization"), "Bearer token-value");
  assert.equal(calls[0].init.headers.get(GRACE_HEADER_NAMES.correlationId), "corr-request");
  assert.equal(calls[0].init.headers.get("Content-Type"), "application/json");
  assert.equal(calls[0].init.body, '{"Name":"sample"}');
});

test("GraceError preserves non-2xx lifecycle headers and unsupported-client details", async () => {
  const client = new GraceClient({
    fetch: async () =>
      jsonResponse(
        {
          Error: "UnsupportedClientVersion",
          CorrelationId: "corr-body",
          Properties: {
            recommendedVersion: "0.2.0",
          },
        },
        {
          "X-Correlation-Id": "corr-header",
          "X-Grace-Client-Support-Status": "unsupported",
          "X-Grace-Client-Unsupported-After": "2026-12-01",
          "X-Grace-Client-Min-Version": "0.1.0",
          "X-Grace-Client-Recommended-Version": "0.2.0",
          "X-Grace-Client-Update-Url": "https://github.com/ScottArbeit/Grace/releases",
          UploadSessionLifecycleState: "Finalized",
          UploadSessionLifecycleReason: "manifest-finalized",
        },
        426,
      ),
  });

  await assert.rejects(
    client.request({ path: "/storage/startUploadSession" }),
    (error) => {
      assert.ok(error instanceof GraceError);
      assert.equal(error.status, 426);
      assert.equal(error.message, "UnsupportedClientVersion");
      assert.equal(error.correlationId, "corr-header");
      assert.equal(error.unsupportedClient, true);
      assert.equal(error.lifecycle.supportStatus, "unsupported");
      assert.equal(error.lifecycle.recommendedVersion, "0.2.0");
      assert.equal(error.lifecycle.uploadSessionState, "Finalized");
      assert.equal(error.lifecycle.uploadSessionReason, "manifest-finalized");
      assert.equal(error.properties.recommendedVersion, "0.2.0");
      return true;
    },
  );
});

test("Tier 2 upload requests a server-issued URI and uploads local bytes without leaking Grace auth", async () => {
  const temp = await mkdtemp(join(tmpdir(), "grace-upload-"));
  const sourcePath = join(temp, "hello.txt");
  await writeFile(sourcePath, "hello grace");

  const calls = [];
  const client = new GraceClient({
    auth: "secret-token",
    correlationId: () => "corr-upload",
    fetch: async (url, init) => {
      calls.push({ init, url });

      if (url.toString() === "http://localhost:5000/storage/getUploadUri") {
        const body = JSON.parse(init.body);
        assert.equal(init.headers.get(GRACE_HEADER_NAMES.apiVersion), "2023-10-01");
        assert.equal(init.headers.get(GRACE_HEADER_NAMES.clientType), GRACE_CLIENT_TYPE);
        assert.equal(init.headers.get(GRACE_HEADER_NAMES.clientVersion), GRACE_CLIENT_VERSION);
        assert.equal(init.headers.get("Authorization"), "Bearer secret-token");
        assert.equal(init.headers.get(GRACE_HEADER_NAMES.correlationId), "corr-upload");
        assert.equal(body.CorrelationId, "corr-upload");
        assert.equal(body.RepositoryName, "repo");
        assert.equal(body.FileVersions[0].RelativePath, "docs/hello.txt");
        assert.equal(body.FileVersions[0].Class, "FileVersion");
        assert.equal(body.FileVersions[0].IsBinary, true);
        assert.equal(body.FileVersions[0].Size, 11);
        assert.match(body.FileVersions[0].Sha256Hash, /^[a-f0-9]{64}$/);
        assert.equal(body.FileVersions[0].ContentReference.ReferenceType, "WholeFileContent");
        return jsonResponse({ "docs/hello.txt": "https://storage.example.test/upload?sig=hidden" }, {
          "X-Correlation-Id": "corr-upload-response",
          "X-Grace-Client-Support-Status": "supported",
        });
      }

      assert.equal(url.toString(), "https://storage.example.test/upload?sig=hidden");
      assert.equal(init.method, "PUT");
      assert.equal(init.headers.get("Authorization"), null);
      assert.equal(init.headers.get(GRACE_HEADER_NAMES.apiVersion), null);
      assert.equal(await init.body.text(), "hello grace");
      return new Response(undefined, {
        headers: { UploadSessionLifecycleState: "WholeFileUploaded" },
        status: 201,
      });
    },
  });

  try {
    const result = await client.uploadFile({
      filePath: sourcePath,
      relativePath: "docs/hello.txt",
      repositoryName: "repo",
    });

    assert.equal(result.relativePath, "docs/hello.txt");
    assert.match(result.sha256Hash, /^[a-f0-9]{64}$/);
    assert.equal(result.size, 11);
    assert.equal(result.uriRequest.correlationId, "corr-upload-response");
    assert.equal(result.uriRequest.lifecycle.supportStatus, "supported");
    assert.equal(result.transfer.status, 201);
    assert.equal(result.transfer.lifecycle.uploadSessionState, "WholeFileUploaded");
    assert.equal(calls.length, 2);
  } finally {
    await rm(temp, { recursive: true, force: true });
  }
});

test("Tier 2 download requests a raw text URI and writes bytes to an existing output path", async () => {
  const temp = await mkdtemp(join(tmpdir(), "grace-download-"));
  const outputPath = join(temp, "hello.txt");

  const calls = [];
  const client = new GraceClient({
    fetch: async (url, init) => {
      calls.push({ init, url });

      if (url.toString() === "http://localhost:5000/storage/getDownloadUri") {
        const body = JSON.parse(init.body);
        assert.equal(init.headers.get("Accept"), "text/plain");
        assert.equal(body.FileVersion.RelativePath, "docs/hello.txt");
        assert.equal(body.FileVersion.Sha256Hash, "abc123");
        return textResponse("https://storage.example.test/download?sig=hidden", {
          "X-Correlation-Id": "corr-download-response",
          "X-Grace-Client-Recommended-Version": "0.0.0-s12",
        });
      }

      assert.equal(url.toString(), "https://storage.example.test/download?sig=hidden");
      assert.equal(init.method, "GET");
      assert.equal(init.headers?.get?.("Authorization") ?? null, null);
      return new Response("downloaded bytes", {
        headers: { UploadSessionLifecycleReason: "whole-file-download" },
      });
    },
  });

  try {
    const result = await client.downloadFile({
      fileVersion: {
        RelativePath: "docs/hello.txt",
        Sha256Hash: "abc123",
        IsBinary: true,
        Size: 16,
      },
      outputPath,
      repositoryName: "repo",
    });

    assert.equal(await readFile(outputPath, "utf8"), "downloaded bytes");
    assert.equal(result.bytesWritten, 16);
    assert.equal(result.uriRequest.body, "https://storage.example.test/download?sig=hidden");
    assert.equal(result.uriRequest.lifecycle.recommendedVersion, "0.0.0-s12");
    assert.equal(result.transfer.lifecycle.uploadSessionReason, "whole-file-download");
    assert.equal(calls.length, 2);
  } finally {
    await rm(temp, { recursive: true, force: true });
  }
});

test("Tier 2 transfer failures are surfaced as GraceError", async () => {
  const temp = await mkdtemp(join(tmpdir(), "grace-transfer-fail-"));
  const sourcePath = join(temp, "hello.txt");
  await writeFile(sourcePath, "hello grace");

  const client = new GraceClient({
    fetch: async (url) => {
      if (url.toString() === "http://localhost:5000/storage/getUploadUri") {
        return jsonResponse({ "docs/hello.txt": "https://storage.example.test/upload?sig=expired" });
      }

      return textResponse("expired", {
        "X-Grace-Client-Support-Status": "unsupported",
      }, 403);
    },
  });

  try {
    await assert.rejects(
      client.uploadFile({
        filePath: sourcePath,
        relativePath: "docs/hello.txt",
        repositoryName: "repo",
      }),
      (error) => {
        assert.ok(error instanceof GraceError);
        assert.equal(error.status, 403);
        assert.equal(error.message, "expired");
        assert.equal(error.unsupportedClient, true);
        return true;
      },
    );
  } finally {
    await rm(temp, { recursive: true, force: true });
  }
});

test("Tier 2 transfer rejects local path problems before contacting Grace", async () => {
  const calls = [];
  const client = new GraceClient({
    fetch: async (...args) => {
      calls.push(args);
      return jsonResponse({});
    },
  });

  await assert.rejects(
    client.uploadFile({
      filePath: join(tmpdir(), "grace-missing-file.txt"),
      relativePath: "docs/missing.txt",
      repositoryName: "repo",
    }),
    /Input file does not exist/,
  );

  await assert.rejects(
    client.downloadFile({
      fileVersion: { RelativePath: "docs/hello.txt", Sha256Hash: "abc123" },
      outputPath: join(tmpdir(), "missing-parent", "hello.txt"),
      repositoryName: "repo",
    }),
    /Output directory does not exist/,
  );

  assert.equal(calls.length, 0);
});

test("Tier 2 transfer honors cancellation signals", async () => {
  const temp = await mkdtemp(join(tmpdir(), "grace-transfer-cancel-"));
  const sourcePath = join(temp, "hello.txt");
  await writeFile(sourcePath, "hello grace");
  const abortController = new AbortController();
  const seenSignals = [];

  const client = new GraceClient({
    fetch: async (url, init) => {
      seenSignals.push(init.signal);

      if (url.toString() === "http://localhost:5000/storage/getUploadUri") {
        abortController.abort();
        return jsonResponse({ "docs/hello.txt": "https://storage.example.test/upload" });
      }

      throw new DOMException("The operation was aborted.", "AbortError");
    },
  });

  try {
    await assert.rejects(
      client.uploadFile({
        filePath: sourcePath,
        relativePath: "docs/hello.txt",
        repositoryName: "repo",
        signal: abortController.signal,
      }),
      /aborted/i,
    );
    assert.deepEqual(seenSignals, [abortController.signal, abortController.signal]);
  } finally {
    await rm(temp, { recursive: true, force: true });
  }
});

function jsonResponse(body, headers = {}, status = 200) {
  return new Response(JSON.stringify(body), {
    headers: {
      "Content-Type": "application/json",
      ...headers,
    },
    status,
  });
}

function textResponse(body, headers = {}, status = 200) {
  return new Response(body, {
    headers: {
      "Content-Type": "text/plain",
      ...headers,
    },
    status,
  });
}
