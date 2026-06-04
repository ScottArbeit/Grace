import {
  bytesEqual,
  bytesToHex,
  computeBlake3Bytes,
  computeChunkAddress,
  computeContentBlockAddress,
  concatBytes,
  GRACE_CONTENT_BLOCK_FORMAT,
  hexToBytes,
  toUint8Array,
  type GraceAddress,
} from "./content-address.js";

export const CONTENT_BLOCK_TRAILER_MAGIC = "GCB1META";
export const CONTENT_BLOCK_FOOTER_MAGIC = "GCB1END!";
export const CONTENT_BLOCK_VERSION = 1;
export const CONTENT_BLOCK_FOOTER_LENGTH = 44;
export const CONTENT_BLOCK_TRAILER_FIXED_LENGTH = 16;
export const CONTENT_BLOCK_CHUNK_RECORD_LENGTH = 44;

const textEncoder = new TextEncoder();
const trailerMagicBytes = textEncoder.encode(CONTENT_BLOCK_TRAILER_MAGIC);
const footerMagicBytes = textEncoder.encode(CONTENT_BLOCK_FOOTER_MAGIC);

export type GraceContentBlockErrorKind = "ContentBlockPayloadInvalid";

export class GraceContentBlockError extends Error {
  public readonly kind: GraceContentBlockErrorKind;

  public constructor(message: string) {
    super(message);
    this.name = "GraceContentBlockError";
    this.kind = "ContentBlockPayloadInvalid";
  }
}

export interface GraceContentBlockChunk {
  readonly physicalOffset: bigint;
  readonly length: number;
  readonly address: GraceAddress;
  readonly bytes: Uint8Array;
}

export interface GraceDecodedContentBlock {
  readonly address: GraceAddress;
  readonly format: typeof GRACE_CONTENT_BLOCK_FORMAT;
  readonly bytes: Uint8Array;
  readonly chunks: readonly GraceContentBlockChunk[];
  readonly payload: Uint8Array;
}

export interface GraceContentBlockChunkInput {
  readonly physicalOffset: number | bigint;
  readonly bytes: BufferSource | string;
}

export interface GraceEncodedContentBlock {
  readonly address: GraceAddress;
  readonly chunks: readonly GraceContentBlockChunk[];
  readonly payload: Uint8Array;
}

export function encodeContentBlock(chunks: readonly GraceContentBlockChunkInput[]): GraceEncodedContentBlock {
  if (chunks.length === 0) {
    throw new GraceContentBlockError("ContentBlock must contain at least one chunk.");
  }

  const normalizedChunks = chunks.map((chunk, index): GraceContentBlockChunk => {
    const bytes = toUint8Array(chunk.bytes);

    if (bytes.byteLength === 0) {
      throw new GraceContentBlockError(`Chunk ${index} must not be empty.`);
    }

    return {
      address: computeChunkAddress(bytes),
      bytes,
      length: bytes.byteLength,
      physicalOffset: normalizePhysicalOffset(chunk.physicalOffset, `chunks[${index}].physicalOffset`),
    };
  });

  const data = concatBytes(normalizedChunks.map((chunk) => chunk.bytes));
  const trailer = encodeTrailer(normalizedChunks);
  const footer = encodeFooter(trailer);
  const payload = concatBytes([data, trailer, footer]);
  const address = computeContentBlockAddress({
    chunkAddresses: normalizedChunks.map((chunk) => chunk.address),
  });

  return {
    address,
    chunks: normalizedChunks,
    payload,
  };
}

export function decodeContentBlock(payload: BufferSource): GraceDecodedContentBlock {
  try {
    const payloadBytes = toUint8Array(payload);

    if (payloadBytes.byteLength < CONTENT_BLOCK_FOOTER_LENGTH + CONTENT_BLOCK_TRAILER_FIXED_LENGTH) {
      throw new Error("Payload is too short to contain a compact ContentBlock footer and trailer.");
    }

    const footerStart = payloadBytes.byteLength - CONTENT_BLOCK_FOOTER_LENGTH;
    const footer = payloadBytes.subarray(footerStart);
    assertMagic(footer.subarray(36, 44), footerMagicBytes, "footer");

    const trailerLength = readUInt32LE(footer, 0);
    if (trailerLength < CONTENT_BLOCK_TRAILER_FIXED_LENGTH) {
      throw new Error("Trailer length is smaller than the fixed trailer header.");
    }

    const trailerStart = footerStart - trailerLength;
    if (trailerStart < 0) {
      throw new Error("Trailer length points before the beginning of the payload.");
    }

    const trailer = payloadBytes.subarray(trailerStart, footerStart);
    const expectedTrailerChecksum = footer.subarray(4, 36);
    const actualTrailerChecksum = computeBlake3Bytes(trailer);
    if (!bytesEqual(actualTrailerChecksum, expectedTrailerChecksum)) {
      throw new Error("Trailer checksum mismatch.");
    }

    assertMagic(trailer.subarray(0, 8), trailerMagicBytes, "trailer");

    const version = readUInt16LE(trailer, 8);
    if (version !== CONTENT_BLOCK_VERSION) {
      throw new Error(`Unsupported ContentBlock trailer version ${version}.`);
    }

    const flags = readUInt16LE(trailer, 10);
    if (flags !== 0) {
      throw new Error("ContentBlock trailer flags must be zero.");
    }

    const chunkCount = readUInt32LE(trailer, 12);
    if (chunkCount === 0) {
      throw new Error("ContentBlock trailer must contain at least one chunk.");
    }

    const expectedTrailerLength = CONTENT_BLOCK_TRAILER_FIXED_LENGTH + chunkCount * CONTENT_BLOCK_CHUNK_RECORD_LENGTH;
    if (trailerLength !== expectedTrailerLength) {
      throw new Error("ContentBlock trailer length does not match the chunk count.");
    }

    const data = payloadBytes.subarray(0, trailerStart);
    const chunks: GraceContentBlockChunk[] = [];
    let dataOffset = 0;

    for (let index = 0; index < chunkCount; index += 1) {
      const recordOffset = CONTENT_BLOCK_TRAILER_FIXED_LENGTH + index * CONTENT_BLOCK_CHUNK_RECORD_LENGTH;
      const physicalOffset = readInt64LE(trailer, recordOffset);
      const length = readUInt32LE(trailer, recordOffset + 8);
      const address = bytesToHex(trailer.subarray(recordOffset + 12, recordOffset + 44));
      const nextDataOffset = dataOffset + length;

      if (length === 0) {
        throw new Error(`Chunk ${index} length must be positive.`);
      }

      if (nextDataOffset > data.byteLength) {
        throw new Error(`Chunk ${index} length exceeds the data section.`);
      }

      const chunkBytes = data.subarray(dataOffset, nextDataOffset);
      const computedChunkAddress = computeChunkAddress(chunkBytes);
      if (computedChunkAddress !== address) {
        throw new Error(`Chunk ${index} address mismatch.`);
      }

      chunks.push({
        address,
        bytes: chunkBytes,
        length,
        physicalOffset,
      });

      dataOffset = nextDataOffset;
    }

    if (dataOffset !== data.byteLength) {
      throw new Error("ContentBlock data section has trailing bytes outside the chunk table.");
    }

    const logicalBytes = concatBytes(chunks.map((chunk) => chunk.bytes));
    const address = computeContentBlockAddress({
      chunkAddresses: chunks.map((chunk) => chunk.address),
    });

    return {
      address,
      bytes: logicalBytes,
      chunks,
      format: GRACE_CONTENT_BLOCK_FORMAT,
      payload: payloadBytes,
    };
  } catch (error) {
    if (error instanceof GraceContentBlockError) {
      throw error;
    }

    throw new GraceContentBlockError(error instanceof Error ? error.message : "Invalid ContentBlock payload.");
  }
}

function encodeTrailer(chunks: readonly GraceContentBlockChunk[]): Uint8Array {
  const trailer = new Uint8Array(CONTENT_BLOCK_TRAILER_FIXED_LENGTH + chunks.length * CONTENT_BLOCK_CHUNK_RECORD_LENGTH);
  trailer.set(trailerMagicBytes, 0);
  writeUInt16LE(trailer, CONTENT_BLOCK_VERSION, 8);
  writeUInt16LE(trailer, 0, 10);
  writeUInt32LE(trailer, chunks.length, 12);

  chunks.forEach((chunk, index) => {
    const recordOffset = CONTENT_BLOCK_TRAILER_FIXED_LENGTH + index * CONTENT_BLOCK_CHUNK_RECORD_LENGTH;
    writeInt64LE(trailer, chunk.physicalOffset, recordOffset);
    writeUInt32LE(trailer, chunk.length, recordOffset + 8);
    trailer.set(hexToBytes(chunk.address, `chunks[${index}].address`), recordOffset + 12);
  });

  return trailer;
}

function encodeFooter(trailer: Uint8Array): Uint8Array {
  const footer = new Uint8Array(CONTENT_BLOCK_FOOTER_LENGTH);
  writeUInt32LE(footer, trailer.byteLength, 0);
  footer.set(computeBlake3Bytes(trailer), 4);
  footer.set(footerMagicBytes, 36);
  return footer;
}

function assertMagic(actual: Uint8Array, expected: Uint8Array, name: string): void {
  if (!bytesEqual(actual, expected)) {
    throw new Error(`Invalid ContentBlock ${name} magic.`);
  }
}

function normalizePhysicalOffset(value: number | bigint, fieldName: string): bigint {
  if (typeof value === "bigint") {
    if (value < 0n || value > 9223372036854775807n) {
      throw new GraceContentBlockError(`${fieldName} must fit a non-negative Int64.`);
    }

    return value;
  }

  if (!Number.isSafeInteger(value) || value < 0) {
    throw new GraceContentBlockError(`${fieldName} must be a safe non-negative integer.`);
  }

  return BigInt(value);
}

function readUInt16LE(bytes: Uint8Array, offset: number): number {
  return new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength).getUint16(offset, true);
}

function readUInt32LE(bytes: Uint8Array, offset: number): number {
  return new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength).getUint32(offset, true);
}

function readInt64LE(bytes: Uint8Array, offset: number): bigint {
  return new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength).getBigInt64(offset, true);
}

function writeUInt16LE(bytes: Uint8Array, value: number, offset: number): void {
  new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength).setUint16(offset, value, true);
}

function writeUInt32LE(bytes: Uint8Array, value: number, offset: number): void {
  if (!Number.isSafeInteger(value) || value < 0 || value > 0xffffffff) {
    throw new GraceContentBlockError("UInt32 value is out of range.");
  }

  new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength).setUint32(offset, value, true);
}

function writeInt64LE(bytes: Uint8Array, value: bigint, offset: number): void {
  new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength).setBigInt64(offset, value, true);
}
