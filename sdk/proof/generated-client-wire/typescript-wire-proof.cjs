const assert = require('node:assert/strict');
const path = require('node:path');

const generatedRoot = process.argv[2];
const { BranchApiDtoFromJSON } = require(path.join(generatedRoot, 'dist', 'models', 'BranchApiDto.js'));
const {
  TypedReferenceApiDtoFromJSON,
  TypedReferenceApiDtoToJSON,
} = require(path.join(generatedRoot, 'dist', 'models', 'TypedReferenceApiDto.js'));
const {
  LibraryContentPreparationDtoFromJSON,
  LibraryContentPreparationDtoToJSON,
} = require(path.join(generatedRoot, 'dist', 'models', 'LibraryContentPreparationDto.js'));
const {
  LibraryNamespaceSlotDtoFromJSON,
  LibraryNamespaceSlotDtoToJSON,
} = require(path.join(generatedRoot, 'dist', 'models', 'LibraryNamespaceSlotDto.js'));
const {
  PrepareLibraryContentReadParametersFromJSON,
  PrepareLibraryContentReadParametersToJSON,
} = require(path.join(generatedRoot, 'dist', 'models', 'PrepareLibraryContentReadParameters.js'));

const zero = '00000000-0000-0000-0000-000000000000';

function normalizeWireReference(reference) {
  const normalized = { ...reference };
  for (const field of ['CreatedAt', 'UpdatedAt', 'DeletedAt']) {
    if (normalized[field] !== undefined) {
      normalized[field] = new Date(normalized[field]).toISOString();
    }
  }

  return JSON.parse(JSON.stringify(normalized));
}

const real = {
  Class: 'ReferenceDto', ReferenceId: '11111111-1111-1111-1111-111111111111',
  OwnerId: '22222222-2222-2222-2222-222222222222', OrganizationId: '33333333-3333-3333-3333-333333333333',
  RepositoryId: '44444444-4444-4444-4444-444444444444', BranchId: '55555555-5555-5555-5555-555555555555',
  DirectoryId: '66666666-6666-6666-6666-666666666666', Sha256Hash: 'a'.repeat(64), Blake3Hash: 'b'.repeat(64),
  ReferenceType: 'Promotion', ReferenceText: 'initial', Links: [], CreatedAt: '2026-07-11T20:00:00Z', DeleteReason: ''
};
const sentinel = {
  Class: 'ReferenceDto', ReferenceId: zero, OwnerId: zero, OrganizationId: zero, RepositoryId: zero,
  BranchId: zero, DirectoryId: zero, Sha256Hash: '', Blake3Hash: '', ReferenceType: 'Save',
  ReferenceText: '', Links: [], CreatedAt: '2000-01-01T00:00:00Z', DeleteReason: ''
};
const branch = BranchApiDtoFromJSON({
  Class: 'BranchDto', BranchId: real.BranchId, BranchName: 'main', ParentBranchId: zero,
  OwnerId: real.OwnerId, OrganizationId: real.OrganizationId, RepositoryId: real.RepositoryId,
  BasedOn: real, UserId: '', AssignEnabled: false, PromotionEnabled: true, CommitEnabled: true,
  CheckpointEnabled: true, SaveEnabled: true, TagEnabled: true, ExternalEnabled: true,
  AutoRebaseEnabled: true, PromotionMode: 'IndividualOnly', LatestReference: real,
  LatestPromotion: real, LatestCommit: sentinel, LatestCheckpoint: sentinel, LatestSave: sentinel,
  ShouldRecomputeLatestReferences: false, CreatedAt: '2026-07-11T20:00:00Z', DeleteReason: ''
});

assert.equal(branch.latestPromotion.referenceId, real.ReferenceId);
assert.equal(branch.latestPromotion.sha256Hash, real.Sha256Hash);
assert.equal(branch.latestPromotion.blake3Hash, real.Blake3Hash);
assert.equal(branch.latestCommit.referenceId, zero);
assert.equal(branch.latestCommit.sha256Hash, '');
assert.deepEqual(
  normalizeWireReference(TypedReferenceApiDtoToJSON(TypedReferenceApiDtoFromJSON(real))),
  normalizeWireReference(real),
);
assert.deepEqual(
  normalizeWireReference(TypedReferenceApiDtoToJSON(TypedReferenceApiDtoFromJSON(sentinel))),
  normalizeWireReference(sentinel),
);
assert.throws(() => TypedReferenceApiDtoFromJSON({
  ...sentinel, OwnerId: real.OwnerId, Sha256Hash: real.Sha256Hash, Blake3Hash: real.Blake3Hash
}), /canonical/);
for (const [field, value] of Object.entries({
  CreatedBy: 'unexpected-principal',
  UpdatedAt: '2026-07-11T20:00:00Z',
  DeletedAt: '2026-07-11T20:00:00Z'
})) {
  assert.throws(() => TypedReferenceApiDtoFromJSON({ ...sentinel, [field]: value }), /canonical/);
}
console.log('TypeScript PascalCase BranchApiDto wire round trip passed');

// Exercise the full generated owner model; an optional file supplies a captured actual HTTP envelope.
const {
  OwnerDirectoryVersionObservationFromJSON,
  OwnerDirectoryVersionObservationToJSON,
} = require(path.join(generatedRoot, 'dist', 'models', 'OwnerDirectoryVersionObservation.js'));
const ownerInputs = process.argv[3]
  ? [JSON.parse(require('node:fs').readFileSync(process.argv[3], 'utf8'))]
  : ['0', '9007199254740993', '9223372036854775807'].map(quantity => ({ ReturnValue: {
      ObservationId: '11111111-1111-1111-1111-111111111111',
      Scope: { OwnerId: real.OwnerId, OrganizationId: real.OrganizationId, RepositoryId: real.RepositoryId },
      DeclaredLogicalBytes: quantity, DistinctContentCount: quantity,
      EnumerationStartedAt: '2026-09-07T01:02:03.123456789Z',
      EnumerationFinishedAt: '2026-09-07T01:02:04.987654321Z',
    } }));
for (const envelope of ownerInputs) {
  const observation = envelope.ReturnValue;
  for (const field of ['DeclaredLogicalBytes', 'DistinctContentCount', 'EnumerationStartedAt', 'EnumerationFinishedAt']) {
    assert.equal(typeof observation[field], 'string', `${field} must arrive as an exact JSON string`);
  }
  assert.deepEqual(OwnerDirectoryVersionObservationToJSON(OwnerDirectoryVersionObservationFromJSON(observation)), observation);
}
console.log(`Owner observation generated codec passed: ${ownerInputs.length} ${process.argv[3] ? 'captured HTTP envelope' : 'synthetic full-schema controls'}`);

const libraryPreparation = {
  UploadSessionId: '77777777-7777-7777-7777-777777777777',
  Blake3Hash: 'c'.repeat(64),
  Sha256Hash: 'd'.repeat(64),
  Size: 123456,
  AuthorizedScope: 'repository:44444444-4444-4444-4444-444444444444',
  StoragePoolId: 'pool-primary',
  ExpiresAt: '2026-07-11T20:15:00Z',
};
const preparation = LibraryContentPreparationDtoFromJSON(libraryPreparation);
assert.equal(preparation.uploadSessionId, libraryPreparation.UploadSessionId);
assert.equal(preparation.authorizedScope, libraryPreparation.AuthorizedScope);
assert.deepEqual(
  LibraryContentPreparationDtoToJSON(preparation),
  { ...libraryPreparation, ExpiresAt: '2026-07-11T20:15:00.000Z' },
);

const librarySlot = {
  Parent: {
    Kind: 'item',
    LibraryPath: 'media',
    ItemId: '88888888-8888-8888-8888-888888888888',
  },
  Name: 'logo.svg',
  SlotVersion: '99999999-9999-9999-9999-999999999999',
  OccupantItemId: 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa',
};
const slot = LibraryNamespaceSlotDtoFromJSON(librarySlot);
assert.equal(slot.parent.kind, 'item');
assert.equal(slot.parent.itemId, librarySlot.Parent.ItemId);
assert.equal(slot.name, librarySlot.Name);
assert.deepEqual(LibraryNamespaceSlotDtoToJSON(slot), librarySlot);

const contentReadRequest = {
  ItemId: librarySlot.OccupantItemId,
  ContentVersionId: 'bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb',
  ContentRevision: 'cursor-revision-3',
};
const readRequest = PrepareLibraryContentReadParametersFromJSON(contentReadRequest);
assert.equal(readRequest.contentRevision, contentReadRequest.ContentRevision);
assert.deepEqual(
  JSON.parse(JSON.stringify(PrepareLibraryContentReadParametersToJSON(readRequest))),
  contentReadRequest,
);

console.log('TypeScript Reference and Library wire round trips passed');
