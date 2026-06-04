import { rawClientMetadata } from "./internal/generated/grace-raw-client-metadata.js";

export interface GraceClientContractMetadata {
  readonly apiContractVersion: string;
  readonly openApiProjectionSha256: string;
}

export class GraceClient {
  public readonly contract: GraceClientContractMetadata;

  public constructor() {
    this.contract = {
      apiContractVersion: rawClientMetadata.apiContractVersion,
      openApiProjectionSha256: rawClientMetadata.openApiProjectionSha256,
    };
  }
}
