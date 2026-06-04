import {
  assertCanonicalGraceAddress,
  computeBlake3Hex,
  computeManifestAddress,
  concatBytes,
  GRACE_CHUNKING_SUITE_ID,
  normalizeNonNegativeInteger,
  type GraceAddress,
  type GraceManifestBlockInput,
} from "./content-address.js";
import { decodeContentBlock, GraceContentBlockError } from "./content-block.js";

export type GraceManifestValidationErrorKind =
  | "EmptyManifest"
  | "InvalidManifestSize"
  | "InvalidChunkingSuiteId"
  | "InvalidFileContentHash"
  | "InvalidManifestAddress"
  | "InvalidContentBlockAddress"
  | "ChunkingSuiteMismatch"
  | "ManifestAddressMismatch"
  | "BlockRangeOutOfOrder"
  | "BlockRangeNotPositive"
  | "MissingContentBlockPayload"
  | "ContentBlockPayloadInvalid"
  | "ContentBlockPayloadSizeMismatch"
  | "FileContentHashMismatch"
  | "ManifestSizeMismatch";

export class GraceManifestValidationError extends Error {
  public readonly kind: GraceManifestValidationErrorKind;

  public constructor(kind: GraceManifestValidationErrorKind, message: string) {
    super(message);
    this.name = "GraceManifestValidationError";
    this.kind = kind;
  }
}

export interface GraceFileManifestBlock extends GraceManifestBlockInput {
  readonly address: GraceAddress;
}

export interface GraceFileManifest {
  readonly manifestAddress: GraceAddress;
  readonly chunkingSuiteId: string;
  readonly fileContentHash: GraceAddress;
  readonly size: number | bigint;
  readonly blocks: readonly GraceFileManifestBlock[];
}

export interface GraceContentBlockPayload {
  readonly address: GraceAddress;
  readonly payload: BufferSource;
}

export interface GraceManifestValidationOptions {
  readonly expectedChunkingSuiteId?: string;
}

export interface GraceManifestValidationResult {
  readonly bytes: Uint8Array;
}

export function validateFileManifest(
  manifest: GraceFileManifest,
  payloads: readonly GraceContentBlockPayload[],
  options: GraceManifestValidationOptions = {},
): GraceManifestValidationResult {
  if (!manifest || typeof manifest !== "object") {
    throw new GraceManifestValidationError("EmptyManifest", "Manifest is required.");
  }

  const size = normalizePositiveInteger(manifest.size, "size", "InvalidManifestSize");
  const expectedChunkingSuiteId = options.expectedChunkingSuiteId ?? GRACE_CHUNKING_SUITE_ID;

  if (!manifest.chunkingSuiteId || manifest.chunkingSuiteId.trim() === "") {
    throw new GraceManifestValidationError("InvalidChunkingSuiteId", "Manifest chunking suite id is required.");
  }

  if (manifest.chunkingSuiteId !== expectedChunkingSuiteId) {
    throw new GraceManifestValidationError("ChunkingSuiteMismatch", "Manifest chunking suite id does not match the expected suite.");
  }

  assertAddress(manifest.fileContentHash, "fileContentHash", "InvalidFileContentHash");
  assertAddress(manifest.manifestAddress, "manifestAddress", "InvalidManifestAddress");

  if (!Array.isArray(manifest.blocks) || manifest.blocks.length === 0) {
    throw new GraceManifestValidationError("EmptyManifest", "Manifest must contain at least one block.");
  }

  let expectedOffset = 0n;
  const normalizedBlocks = manifest.blocks.map((block, index) => {
    assertAddress(block.address, `blocks[${index}].address`, "InvalidContentBlockAddress");
    const offset = normalizeNonNegativeInteger(block.offset, `blocks[${index}].offset`);
    const blockSize = normalizePositiveInteger(block.size, `blocks[${index}].size`, "BlockRangeNotPositive");

    if (offset !== expectedOffset) {
      throw new GraceManifestValidationError("BlockRangeOutOfOrder", `Block ${index} is not contiguous from offset ${expectedOffset}.`);
    }

    expectedOffset += blockSize;

    return {
      address: block.address,
      offset,
      size: blockSize,
    };
  });

  if (expectedOffset !== size) {
    throw new GraceManifestValidationError("ManifestSizeMismatch", "Manifest block ranges do not cover the declared manifest size.");
  }

  const recomputedManifestAddress = computeManifestAddress({
    blocks: normalizedBlocks,
    chunkingSuiteId: manifest.chunkingSuiteId,
    fileContentHash: manifest.fileContentHash,
    size,
  });

  if (recomputedManifestAddress !== manifest.manifestAddress) {
    throw new GraceManifestValidationError("ManifestAddressMismatch", "Manifest address does not match the reconstruction contract.");
  }

  const payloadsByAddress = new Map<GraceAddress, Uint8Array>();

  for (const [index, payload] of payloads.entries()) {
    assertAddress(payload.address, `payloads[${index}].address`, "InvalidContentBlockAddress");

    try {
      const decoded = decodeContentBlock(payload.payload);
      if (decoded.address !== payload.address) {
        throw new GraceManifestValidationError("ContentBlockPayloadInvalid", `ContentBlock payload ${index} address does not match its decoded address.`);
      }

      payloadsByAddress.set(payload.address, decoded.bytes);
    } catch (error) {
      if (error instanceof GraceManifestValidationError) {
        throw error;
      }

      if (error instanceof GraceContentBlockError) {
        throw new GraceManifestValidationError("ContentBlockPayloadInvalid", error.message);
      }

      throw new GraceManifestValidationError("ContentBlockPayloadInvalid", "ContentBlock payload is invalid.");
    }
  }

  const reconstructedBlocks = normalizedBlocks.map((block, index) => {
    const decodedBytes = payloadsByAddress.get(block.address);
    if (!decodedBytes) {
      throw new GraceManifestValidationError("MissingContentBlockPayload", `Missing ContentBlock payload for block ${index}.`);
    }

    if (BigInt(decodedBytes.byteLength) !== block.size) {
      throw new GraceManifestValidationError("ContentBlockPayloadSizeMismatch", `Decoded ContentBlock size mismatch for block ${index}.`);
    }

    return decodedBytes;
  });

  const bytes = concatBytes(reconstructedBlocks);
  if (BigInt(bytes.byteLength) !== size) {
    throw new GraceManifestValidationError("ManifestSizeMismatch", "Reconstructed bytes do not match manifest size.");
  }

  if (computeBlake3Hex(bytes) !== manifest.fileContentHash) {
    throw new GraceManifestValidationError("FileContentHashMismatch", "Reconstructed bytes do not match the manifest file content hash.");
  }

  return { bytes };
}

function assertAddress(address: string, fieldName: string, kind: GraceManifestValidationErrorKind): void {
  try {
    assertCanonicalGraceAddress(address, fieldName);
  } catch (error) {
    throw new GraceManifestValidationError(kind, error instanceof Error ? error.message : `${fieldName} is invalid.`);
  }
}

function normalizePositiveInteger(
  value: number | bigint,
  fieldName: string,
  kind: GraceManifestValidationErrorKind,
): bigint {
  let normalized: bigint;

  try {
    normalized = normalizeNonNegativeInteger(value, fieldName);
  } catch (error) {
    throw new GraceManifestValidationError(kind, error instanceof Error ? error.message : `${fieldName} is invalid.`);
  }

  if (normalized <= 0n) {
    throw new GraceManifestValidationError(kind, `${fieldName} must be positive.`);
  }

  return normalized;
}
