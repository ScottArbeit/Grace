import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { test } from "node:test";
import {
  computeBlake3Hex,
  computeChunkAddress,
  computeContentBlockAddress,
  computeManifestAddress,
  contentBlockPreimage,
  decideManifestEligibility,
  decodeContentBlock,
  detectBinaryByNulScan,
  encodeContentBlock,
  GraceManifestValidationError,
  GRACE_CONTENT_BLOCK_FORMAT,
  GRACE_PROTOCOL_VECTOR_SUITE,
  isCanonicalGraceAddress,
  manifestPreimage,
  validateFileManifest,
} from "../dist/index.js";

const packageRoot = resolve(dirname(fileURLToPath(import.meta.url)), "..");
const repoRoot = resolve(packageRoot, "../../..");
const vectorRoot = resolve(repoRoot, "test-vectors/protocol");
const textEncoder = new TextEncoder();
const textDecoder = new TextDecoder();

test("public exports include protocol helpers and vector suite metadata", async () => {
  const exports = Object.keys(await import("../dist/index.js")).sort();

  assert.ok(exports.includes("GRACE_PROTOCOL_VECTOR_SUITE"));
  assert.ok(exports.includes("computeBlake3Hex"));
  assert.ok(exports.includes("decodeContentBlock"));
  assert.ok(exports.includes("validateFileManifest"));
  assert.equal(GRACE_PROTOCOL_VECTOR_SUITE, "grace-protocol-v1");
});

test("content address vectors match Grace Protocol v1", async () => {
  const root = await loadVector("content-addresses.v1.json");
  const vectors = root.vectors;

  assert.equal(root.schemaVersion, 1);
  assert.equal(root.protocolVersion, GRACE_PROTOCOL_VECTOR_SUITE);

  for (const vector of vectors.blake3) {
    assert.equal(computeBlake3Hex(vector.utf8), vector.hash, vector.name);
  }

  for (const address of vectors.addressValidation.valid) {
    assert.equal(isCanonicalGraceAddress(address), true, address);
  }

  for (const address of vectors.addressValidation.invalid) {
    assert.equal(isCanonicalGraceAddress(address), false, address);
  }

  const contentBlock = vectors.contentBlock;
  const chunkAddresses = contentBlock.chunks.map((chunk) => {
    const actualAddress = computeChunkAddress(chunk.utf8);
    assert.equal(actualAddress, chunk.address, `chunk ${chunk.ordinal}`);
    return actualAddress;
  });

  assert.equal(contentBlock.format, GRACE_CONTENT_BLOCK_FORMAT);
  assert.equal(contentBlockPreimage({ chunkAddresses, format: contentBlock.format }), contentBlock.preimage);
  assert.equal(computeContentBlockAddress({ chunkAddresses, format: contentBlock.format }), contentBlock.address);
  assert.equal(
    computeContentBlockAddress({ chunkAddresses: [...chunkAddresses].reverse(), format: contentBlock.format }),
    contentBlock.reorderedAddress,
  );

  const fileManifest = vectors.fileManifest;
  assert.equal(manifestPreimage(fileManifest), fileManifest.preimage);
  assert.equal(computeManifestAddress(fileManifest), fileManifest.manifestAddress);
});

test("ContentBlock payload vectors decode and reject corruption", async () => {
  const root = await loadVector("manifest-validation.v1.json");

  for (const vector of root.contentBlocks) {
    const payload = Buffer.from(vector.payloadBase64, "base64");
    const decoded = decodeContentBlock(payload);

    assert.equal(decoded.address, vector.address, vector.name);
    assert.equal(decoded.bytes.byteLength, vector.logicalSize, vector.name);
    assert.equal(textDecoder.decode(decoded.bytes), vector.utf8, vector.name);

    const encoded = encodeContentBlock([
      {
        bytes: vector.utf8,
        physicalOffset: vector.physicalOffset,
      },
    ]);

    assert.equal(encoded.address, vector.address, vector.name);
    assert.equal(Buffer.from(encoded.payload).toString("base64"), vector.payloadBase64, vector.name);
  }

  const tampered = Buffer.from(root.contentBlocks[0].payloadBase64, "base64");
  tampered[0] ^= 0x01;
  assert.throws(() => decodeContentBlock(tampered), /Chunk 0 address mismatch|Trailer checksum mismatch/);

  const badFooter = Buffer.from(root.contentBlocks[0].payloadBase64, "base64");
  badFooter[badFooter.length - 1] ^= 0x01;
  assert.throws(() => decodeContentBlock(badFooter), /footer magic/);
});

test("manifest validation reconstructs valid vector and rejects corruption cases", async () => {
  const root = await loadVector("manifest-validation.v1.json");
  const manifest = createManifestFromVector(root);
  const payloads = payloadsFromVector(root);

  const result = validateFileManifest(manifest, payloads, {
    expectedChunkingSuiteId: root.chunkingSuiteId,
  });

  assert.equal(textDecoder.decode(result.bytes), root.success.expectedUtf8);

  const expectedErrors = new Map(root.rejectionCases.map((item) => [item.name, item.expectedErrorKind]));
  const cases = createRejectionCases(root, manifest, payloads);

  for (const [name, rejectedManifest, rejectedPayloads] of cases) {
    assert.throws(
      () => validateFileManifest(rejectedManifest, rejectedPayloads, { expectedChunkingSuiteId: root.chunkingSuiteId }),
      (error) => {
        assert.ok(error instanceof GraceManifestValidationError, name);
        assert.equal(error.kind, expectedErrors.get(name), name);
        return true;
      },
      name,
    );
  }
});

test("eligibility vectors document the default Tier 3 boundary", async () => {
  const root = await loadVector("eligibility.v1.json");

  for (const vector of root.vectors) {
    const actual = decideManifestEligibility({
      compressedSize: vector.compressedSize,
      isBinary: vector.kind === "binary",
      uncompressedSize: vector.uncompressedSize,
    });

    assert.equal(actual, vector.expectedReferenceType, vector.name);
  }

  assert.equal(detectBinaryByNulScan(Uint8Array.of(1, 0, 2)), true);

  const nulAfterScanWindow = new Uint8Array(8193);
  nulAfterScanWindow.fill(65);
  nulAfterScanWindow[8192] = 0;
  assert.equal(detectBinaryByNulScan(nulAfterScanWindow), false);
});

async function loadVector(fileName) {
  return JSON.parse(await readFile(resolve(vectorRoot, fileName), "utf8"));
}

function createManifestFromVector(root) {
  const blocksByName = new Map(root.contentBlocks.map((block) => [block.name, block]));

  return {
    blocks: root.manifest.blocks.map((block) => {
      const contentBlock = blocksByName.get(block.name);
      assert.ok(contentBlock, `missing block vector ${block.name}`);

      return {
        address: contentBlock.address,
        offset: block.offset,
        size: block.size,
      };
    }),
    chunkingSuiteId: root.chunkingSuiteId,
    fileContentHash: root.file.fileContentHash,
    manifestAddress: root.manifest.manifestAddress,
    size: root.manifest.size,
  };
}

function payloadsFromVector(root) {
  return root.contentBlocks.map((block) => ({
    address: block.address,
    payload: Buffer.from(block.payloadBase64, "base64"),
  }));
}

function createRejectionCases(root, manifest, payloads) {
  const alpha = root.contentBlocks.find((block) => block.name === "alpha");
  const beta = root.contentBlocks.find((block) => block.name === "beta");
  assert.ok(alpha);
  assert.ok(beta);

  const tamperedPayload = Buffer.from(alpha.payloadBase64, "base64");
  tamperedPayload[0] ^= 0x01;

  const invalidFooter = Buffer.from(alpha.payloadBase64, "base64");
  invalidFooter[invalidFooter.length - 1] ^= 0x01;

  const staleManifest = {
    ...manifest,
    fileContentHash: computeBlake3Hex("different bytes"),
  };

  const hashMismatch = {
    ...staleManifest,
    manifestAddress: computeManifestAddress(staleManifest),
  };

  const reorderedManifest = {
    ...manifest,
    blocks: [
      {
        address: beta.address,
        offset: alpha.logicalSize,
        size: beta.logicalSize,
      },
      {
        address: alpha.address,
        offset: 0,
        size: alpha.logicalSize,
      },
    ],
  };

  const mismatchedPayloads = [
    {
      address: manifest.manifestAddress,
      payload: Buffer.from(alpha.payloadBase64, "base64"),
    },
    payloads.find((payload) => payload.address === beta.address),
  ];

  return [
    [
      "tampered-payload-byte",
      manifest,
      [
        {
          address: alpha.address,
          payload: tamperedPayload,
        },
        payloads.find((payload) => payload.address === beta.address),
      ],
    ],
    [
      "invalid-compact-block-footer",
      manifest,
      [
        {
          address: alpha.address,
          payload: invalidFooter,
        },
        payloads.find((payload) => payload.address === beta.address),
      ],
    ],
    ["reordered-manifest-blocks", reorderedManifest, payloads],
    ["stale-manifest-address", staleManifest, payloads],
    ["file-content-hash-mismatch", hashMismatch, payloads],
    ["missing-content-block", manifest, [payloads.find((payload) => payload.address === alpha.address)]],
    ["content-block-address-mismatch", manifest, mismatchedPayloads],
  ];
}
