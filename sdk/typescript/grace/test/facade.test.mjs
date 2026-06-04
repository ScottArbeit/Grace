import assert from "node:assert/strict";
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
    "GRACE_CLIENT_TYPE",
    "GRACE_CLIENT_VERSION",
    "GRACE_HEADER_NAMES",
    "GraceClient",
    "GraceError",
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

test("Tier 2 file transfer is explicitly out of scope", () => {
  const client = new GraceClient({ fetch: async () => jsonResponse({}) });

  assert.throws(() => client.unsupportedFileTransfer(), /Tier 2 file transfer is not implemented/);
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
