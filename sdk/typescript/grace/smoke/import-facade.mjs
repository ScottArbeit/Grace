import { GraceClient } from "@grace/sdk";

const client = new GraceClient();

if (!client.contract.apiContractVersion) {
  throw new Error("GraceClient facade did not expose API contract metadata.");
}

if (!client.contract.openApiProjectionSha256 || client.contract.openApiProjectionSha256.length !== 64) {
  throw new Error("GraceClient facade did not expose OpenAPI projection provenance.");
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
