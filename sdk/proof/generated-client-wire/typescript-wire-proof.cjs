const assert = require('node:assert/strict');
const path = require('node:path');

const generatedRoot = process.argv[2];
const { BranchApiDtoFromJSON } = require(path.join(generatedRoot, 'dist', 'models', 'BranchApiDto.js'));
const { TypedReferenceApiDtoFromJSON } = require(path.join(generatedRoot, 'dist', 'models', 'TypedReferenceApiDto.js'));

const zero = '00000000-0000-0000-0000-000000000000';
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
assert.throws(() => TypedReferenceApiDtoFromJSON({
  ...sentinel, OwnerId: real.OwnerId, Sha256Hash: real.Sha256Hash, Blake3Hash: real.Blake3Hash
}), /canonical/);
console.log('TypeScript PascalCase BranchApiDto wire round trip passed');
