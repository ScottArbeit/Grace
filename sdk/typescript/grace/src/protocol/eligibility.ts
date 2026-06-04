export const DEFAULT_MANIFEST_ELIGIBILITY_POLICY = {
  binaryScanBytes: 8192,
  class: "ManifestEligibilityPolicy",
  thresholdBytes: 1048576,
} as const;

export type GraceContentReferenceType = "WholeFileContent" | "FileManifest";

export interface GraceManifestEligibilityInput {
  readonly uncompressedSize: number | bigint;
  readonly compressedSize?: number | bigint | null;
  readonly isBinary?: boolean;
}

export function detectBinaryByNulScan(bytes: BufferSource, scanBytes = DEFAULT_MANIFEST_ELIGIBILITY_POLICY.binaryScanBytes): boolean {
  const view = bytes instanceof ArrayBuffer
    ? new Uint8Array(bytes)
    : new Uint8Array(bytes.buffer, bytes.byteOffset, bytes.byteLength);
  const limit = Math.min(view.byteLength, scanBytes);

  for (let index = 0; index < limit; index += 1) {
    if (view[index] === 0) {
      return true;
    }
  }

  return false;
}

export function decideManifestEligibility(input: GraceManifestEligibilityInput): GraceContentReferenceType {
  const uncompressedSize = normalizeSize(input.uncompressedSize, "uncompressedSize");
  const isBinary = input.isBinary ?? false;
  const relevantSize = isBinary
    ? uncompressedSize
    : normalizeSize(input.compressedSize ?? input.uncompressedSize, "compressedSize");

  return relevantSize >= BigInt(DEFAULT_MANIFEST_ELIGIBILITY_POLICY.thresholdBytes) ? "FileManifest" : "WholeFileContent";
}

function normalizeSize(value: number | bigint, fieldName: string): bigint {
  if (typeof value === "bigint") {
    if (value < 0n) {
      throw new Error(`${fieldName} must be non-negative.`);
    }

    return value;
  }

  if (!Number.isSafeInteger(value) || value < 0) {
    throw new Error(`${fieldName} must be a safe non-negative integer.`);
  }

  return BigInt(value);
}
