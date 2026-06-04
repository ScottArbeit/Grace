import { GraceClient } from "../dist/index.js";

const client = new GraceClient();

if (!client.contract.apiContractVersion) {
  throw new Error("GraceClient facade did not expose API contract metadata.");
}

if (!client.contract.openApiProjectionSha256 || client.contract.openApiProjectionSha256.length !== 64) {
  throw new Error("GraceClient facade did not expose OpenAPI projection provenance.");
}

console.log(`TypeScript facade import OK (${client.contract.apiContractVersion}).`);
