import { blake3 } from "@noble/hashes/blake3.js";

export const GRACE_PROTOCOL_VERSION = "grace-protocol-v1";
export const GRACE_PROTOCOL_VECTOR_SUITE = "grace-protocol-v1";
export const GRACE_CONTENT_BLOCK_FORMAT = "grace-contentblock-v1";
export const GRACE_CHUNKING_SUITE_ID = "grace-rabin-blake3-64k-v1";

const canonicalAddressPattern = /^[0-9a-f]{64}$/;
const textEncoder = new TextEncoder();

export type GraceAddress = string;

export interface GraceContentBlockAddressInput {
  readonly format?: string;
  readonly chunkAddresses: readonly string[];
}

export interface GraceManifestBlockInput {
  readonly address: string;
  readonly offset: number | bigint;
  readonly size: number | bigint;
}

export interface GraceManifestAddressInput {
  readonly chunkingSuiteId: string;
  readonly fileContentHash: string;
  readonly size: number | bigint;
  readonly blocks: readonly GraceManifestBlockInput[];
}

export function isCanonicalGraceAddress(address: unknown): address is GraceAddress {
  return typeof address === "string" && canonicalAddressPattern.test(address);
}

export function assertCanonicalGraceAddress(address: unknown, fieldName = "address"): GraceAddress {
  if (!isCanonicalGraceAddress(address)) {
    throw new Error(`${fieldName} must be a lowercase 64-character hexadecimal Grace address.`);
  }

  return address;
}

export function computeBlake3Hex(bytes: BufferSource | string): GraceAddress {
  return bytesToHex(computeBlake3Bytes(bytes));
}

export function computeBlake3Bytes(bytes: BufferSource | string): Uint8Array {
  return blake3(toUint8Array(bytes));
}

export function computeChunkAddress(bytes: BufferSource | string): GraceAddress {
  return computeBlake3Hex(bytes);
}

export function contentBlockPreimage(input: GraceContentBlockAddressInput): string {
  const format = input.format ?? GRACE_CONTENT_BLOCK_FORMAT;

  if (!format) {
    throw new Error("ContentBlock format is required.");
  }

  let preimage = "grace.cas.v1.content-block\n";
  preimage += `format:${format}\n`;
  preimage += `chunk-count:${input.chunkAddresses.length}\n`;

  input.chunkAddresses.forEach((address, index) => {
    assertCanonicalGraceAddress(address, `chunkAddresses[${index}]`);
    preimage += `chunk:${index}:${address}\n`;
  });

  return preimage;
}

export function computeContentBlockAddress(input: GraceContentBlockAddressInput): GraceAddress {
  return computeBlake3Hex(contentBlockPreimage(input));
}

export function manifestPreimage(input: GraceManifestAddressInput): string {
  assertCanonicalGraceAddress(input.fileContentHash, "fileContentHash");
  const size = normalizeNonNegativeInteger(input.size, "size");

  if (!input.chunkingSuiteId || input.chunkingSuiteId.trim() === "") {
    throw new Error("chunkingSuiteId is required.");
  }

  let preimage = "grace.cas.v1.file-manifest\n";
  preimage += `chunking-suite:${input.chunkingSuiteId}\n`;
  preimage += `file-content-hash:${input.fileContentHash}\n`;
  preimage += `size:${size}\n`;
  preimage += `block-count:${input.blocks.length}\n`;

  input.blocks.forEach((block, index) => {
    assertCanonicalGraceAddress(block.address, `blocks[${index}].address`);
    const offset = normalizeNonNegativeInteger(block.offset, `blocks[${index}].offset`);
    const blockSize = normalizeNonNegativeInteger(block.size, `blocks[${index}].size`);
    preimage += `block:${index}:${block.address}:${offset}:${blockSize}\n`;
  });

  return preimage;
}

export function computeManifestAddress(input: GraceManifestAddressInput): GraceAddress {
  return computeBlake3Hex(manifestPreimage(input));
}

export function bytesToHex(bytes: Uint8Array): string {
  return Array.from(bytes, (byte) => byte.toString(16).padStart(2, "0")).join("");
}

export function hexToBytes(hex: string, fieldName = "hex"): Uint8Array {
  assertCanonicalGraceAddress(hex, fieldName);
  const bytes = new Uint8Array(hex.length / 2);

  for (let index = 0; index < hex.length; index += 2) {
    bytes[index / 2] = Number.parseInt(hex.slice(index, index + 2), 16);
  }

  return bytes;
}

export function bytesEqual(left: Uint8Array, right: Uint8Array): boolean {
  if (left.byteLength !== right.byteLength) {
    return false;
  }

  let difference = 0;
  for (let index = 0; index < left.byteLength; index += 1) {
    difference |= left[index] ^ right[index];
  }

  return difference === 0;
}

export function concatBytes(chunks: readonly Uint8Array[]): Uint8Array {
  const totalLength = chunks.reduce((sum, chunk) => sum + chunk.byteLength, 0);
  const result = new Uint8Array(totalLength);
  let offset = 0;

  for (const chunk of chunks) {
    result.set(chunk, offset);
    offset += chunk.byteLength;
  }

  return result;
}

export function toUint8Array(bytes: BufferSource | string): Uint8Array {
  if (typeof bytes === "string") {
    return textEncoder.encode(bytes);
  }

  if (bytes instanceof ArrayBuffer) {
    return new Uint8Array(bytes);
  }

  return new Uint8Array(bytes.buffer, bytes.byteOffset, bytes.byteLength);
}

export function normalizeNonNegativeInteger(value: number | bigint, fieldName: string): bigint {
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
