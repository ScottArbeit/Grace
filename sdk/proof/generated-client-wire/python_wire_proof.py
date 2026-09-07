import json
import sys
from uuid import UUID

sys.path.insert(0, sys.argv[1])

from grace_generated_openapi_probe.models.reference_api_dto import ReferenceApiDto
from grace_generated_openapi_probe.models.reference_default_sentinel import ReferenceDefaultSentinel
from grace_generated_openapi_probe.models.typed_reference_api_dto import TypedReferenceApiDto
from grace_generated_openapi_probe.models.library_content_preparation_dto import LibraryContentPreparationDto
from grace_generated_openapi_probe.models.library_namespace_slot_dto import LibraryNamespaceSlotDto
from grace_generated_openapi_probe.models.prepare_library_content_read_parameters import PrepareLibraryContentReadParameters

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
assert json.loads(TypedReferenceApiDto.from_dict(REAL).to_json()) == REAL
assert json.loads(TypedReferenceApiDto.from_dict(SENTINEL).to_json()) == SENTINEL

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

try:
    TypedReferenceApiDto.from_dict({**SENTINEL, "Links": ["unexpected-link"]})
except ValueError:
    pass
else:
    raise AssertionError("sentinel with non-canonical Links was accepted")

library_preparation = {
    "UploadSessionId": "77777777-7777-7777-7777-777777777777",
    "Blake3Hash": "c" * 64,
    "Sha256Hash": "d" * 64,
    "Size": 123456,
    "AuthorizedScope": "repository:44444444-4444-4444-4444-444444444444",
    "StoragePoolId": "pool-primary",
    "ExpiresAt": "2026-07-11T20:15:00Z",
}
preparation = LibraryContentPreparationDto.from_dict(library_preparation)
assert preparation.upload_session_id == UUID(library_preparation["UploadSessionId"])
assert preparation.authorized_scope == library_preparation["AuthorizedScope"]
assert json.loads(preparation.to_json()) == library_preparation

library_slot = {
    "Parent": {
        "Kind": "item",
        "LibraryPath": "media",
        "ItemId": "88888888-8888-8888-8888-888888888888",
    },
    "Name": "logo.svg",
    "SlotVersion": "99999999-9999-9999-9999-999999999999",
    "OccupantItemId": "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
}
slot = LibraryNamespaceSlotDto.from_dict(library_slot)
assert slot.parent.kind == "item"
assert slot.parent.item_id == UUID(library_slot["Parent"]["ItemId"])
assert slot.name == library_slot["Name"]
assert json.loads(slot.to_json()) == library_slot

content_read_request = {
    "ItemId": library_slot["OccupantItemId"],
    "ContentVersionId": "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb",
    "ContentRevision": "cursor-revision-3",
}
read_request = PrepareLibraryContentReadParameters.from_dict(content_read_request)
assert read_request.content_revision == content_read_request["ContentRevision"]
assert json.loads(read_request.to_json()) == content_read_request

print("Python Reference and Library wire round trips passed")
