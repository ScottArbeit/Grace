import sys
from uuid import UUID

sys.path.insert(0, sys.argv[1])

from grace_generated_openapi_probe.models.reference_api_dto import ReferenceApiDto
from grace_generated_openapi_probe.models.reference_default_sentinel import ReferenceDefaultSentinel
from grace_generated_openapi_probe.models.typed_reference_api_dto import TypedReferenceApiDto

ZERO = "00000000-0000-0000-0000-000000000000"
REAL = {
    "Class": "ReferenceDto", "ReferenceId": "11111111-1111-1111-1111-111111111111",
    "OwnerId": "22222222-2222-2222-2222-222222222222", "OrganizationId": "33333333-3333-3333-3333-333333333333",
    "RepositoryId": "44444444-4444-4444-4444-444444444444", "BranchId": "55555555-5555-5555-5555-555555555555",
    "DirectoryId": "66666666-6666-6666-6666-666666666666", "Sha256Hash": "a" * 64, "Blake3Hash": "b" * 64,
    "ReferenceType": "Promotion", "ReferenceText": "initial", "Links": [], "CreatedAt": "2026-07-11T20:00:00Z", "DeleteReason": ""
}
SENTINEL = {
    "Class": "ReferenceDto", "ReferenceId": ZERO, "OwnerId": ZERO, "OrganizationId": ZERO, "RepositoryId": ZERO,
    "BranchId": ZERO, "DirectoryId": ZERO, "Sha256Hash": "", "Blake3Hash": "", "ReferenceType": "Save",
    "ReferenceText": "", "Links": [], "CreatedAt": "2000-01-01T00:00:00Z", "DeleteReason": ""
}

real = TypedReferenceApiDto.from_dict(REAL).actual_instance
sentinel = TypedReferenceApiDto.from_dict(SENTINEL).actual_instance
assert isinstance(real, ReferenceApiDto)
assert real.reference_id == UUID(REAL["ReferenceId"])
assert real.sha256_hash == REAL["Sha256Hash"] and real.blake3_hash == REAL["Blake3Hash"]
assert isinstance(sentinel, ReferenceDefaultSentinel)
assert sentinel.reference_id == UUID(ZERO)

try:
    TypedReferenceApiDto.from_dict({
        **SENTINEL, "OwnerId": REAL["OwnerId"],
        "Sha256Hash": REAL["Sha256Hash"], "Blake3Hash": REAL["Blake3Hash"]
    })
except ValueError:
    pass
else:
    raise AssertionError("partial sentinel was accepted")

for field, value in {
    "CreatedBy": "unexpected-principal",
    "UpdatedAt": "2026-07-11T20:00:00Z",
    "DeletedAt": "2026-07-11T20:00:00Z",
}.items():
    try:
        TypedReferenceApiDto.from_dict({**SENTINEL, field: value})
    except ValueError:
        pass
    else:
        raise AssertionError(f"sentinel with non-canonical {field} was accepted")

print("Python UUID-coerced typed Reference wire round trip passed")
