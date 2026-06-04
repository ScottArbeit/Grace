import { GraceClient, GraceError } from "@grace/sdk";

const client = new GraceClient();

if (!client.contract.apiContractVersion) {
  throw new Error("GraceClient facade did not expose API contract metadata.");
}

if (!client.contract.openApiProjectionSha256 || client.contract.openApiProjectionSha256.length !== 64) {
  throw new Error("GraceClient facade did not expose OpenAPI projection provenance.");
}

if (client.apiVersion !== "2023-10-01") {
  throw new Error(`GraceClient facade defaulted to unexpected API version: ${client.apiVersion}.`);
}

if (typeof GraceError !== "function") {
  throw new Error("GraceError facade export was not available.");
}

try {
  await import("@grace/sdk/internal/generated/grace-raw-client-metadata");
  throw new Error("Internal generated TypeScript metadata subpath was importable through package exports.");
} catch (error) {
  if (error?.code !== "ERR_PACKAGE_PATH_NOT_EXPORTED") {
    throw error;
  }
}

console.log(`TypeScript facade import OK (${client.contract.apiContractVersion}).`);
