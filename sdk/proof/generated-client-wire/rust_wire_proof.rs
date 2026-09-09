use grace_generated_openapi_probe::models::{
    LibraryContentPreparationDto, LibraryNamespaceSlotDto, PrepareLibraryContentReadParameters,
    TypedReferenceApiDto,
};
use serde_json::json;

fn sentinel() -> serde_json::Value {
    let zero = "00000000-0000-0000-0000-000000000000";
    json!({"Class":"ReferenceDto","ReferenceId":zero,"OwnerId":zero,"OrganizationId":zero,
        "RepositoryId":zero,"BranchId":zero,"DirectoryId":zero,"Sha256Hash":"","Blake3Hash":"",
        "ReferenceType":"Save","ReferenceText":"","Links":[],"CreatedAt":"2000-01-01T00:00:00Z","DeleteReason":""})
}

#[test]
fn typed_reference_wire_variants_are_semantic() {
    let sentinel_value = sentinel();
    let decoded: TypedReferenceApiDto = serde_json::from_value(sentinel_value.clone()).unwrap();
    assert!(matches!(decoded, TypedReferenceApiDto::ReferenceDefaultSentinel(_)));
    assert_eq!(serde_json::to_value(decoded).unwrap(), sentinel_value);

    let real = json!({"Class":"ReferenceDto","ReferenceId":"11111111-1111-1111-1111-111111111111",
        "OwnerId":"22222222-2222-2222-2222-222222222222","OrganizationId":"33333333-3333-3333-3333-333333333333",
        "RepositoryId":"44444444-4444-4444-4444-444444444444","BranchId":"55555555-5555-5555-5555-555555555555",
        "DirectoryId":"66666666-6666-6666-6666-666666666666","Sha256Hash":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
        "Blake3Hash":"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
        "ReferenceType":"Promotion","ReferenceText":"initial","Links":[],"CreatedAt":"2026-07-11T20:00:00Z","DeleteReason":""});
    let decoded: TypedReferenceApiDto = serde_json::from_value(real.clone()).unwrap();
    assert!(matches!(decoded, TypedReferenceApiDto::ReferenceApiDto(_)));
    assert_eq!(serde_json::to_value(decoded).unwrap(), real);

    let mut partial = sentinel();
    partial["OwnerId"] = json!("22222222-2222-2222-2222-222222222222");
    partial["Sha256Hash"] = json!("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
    partial["Blake3Hash"] = json!("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
    assert!(serde_json::from_value::<TypedReferenceApiDto>(partial).is_err());

    for (field, value) in [
        ("CreatedBy", json!("unexpected-principal")),
        ("UpdatedAt", json!("2026-07-11T20:00:00Z")),
        ("DeletedAt", json!("2026-07-11T20:00:00Z")),
    ] {
        let mut non_canonical = sentinel();
        non_canonical[field] = value;
        assert!(serde_json::from_value::<TypedReferenceApiDto>(non_canonical).is_err());
    }

    let mut non_canonical = sentinel();
    non_canonical["Links"] = json!(["unexpected-link"]);
    assert!(serde_json::from_value::<TypedReferenceApiDto>(non_canonical).is_err());
}

#[test]
fn library_preparation_parent_name_and_content_revision_round_trip() {
    let preparation = json!({
        "UploadSessionId":"77777777-7777-7777-7777-777777777777",
        "Blake3Hash":"cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc",
        "Sha256Hash":"dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd",
        "Size":123456,
        "AuthorizedScope":"repository:44444444-4444-4444-4444-444444444444",
        "StoragePoolId":"pool-primary",
        "ExpiresAt":"2026-07-11T20:15:00Z"
    });
    let decoded: LibraryContentPreparationDto = serde_json::from_value(preparation.clone()).unwrap();
    assert_eq!(decoded.upload_session_id.to_string(), "77777777-7777-7777-7777-777777777777");
    assert_eq!(decoded.authorized_scope, "repository:44444444-4444-4444-4444-444444444444");
    assert_eq!(serde_json::to_value(decoded).unwrap(), preparation);

    let slot = json!({
        "Parent":{
            "Kind":"item",
            "LibraryPath":"media",
            "ItemId":"88888888-8888-8888-8888-888888888888"
        },
        "Name":"logo.svg",
        "SlotVersion":"99999999-9999-9999-9999-999999999999",
        "OccupantItemId":"aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"
    });
    let decoded: LibraryNamespaceSlotDto = serde_json::from_value(slot.clone()).unwrap();
    assert!(matches!(
        decoded.parent.kind,
        grace_generated_openapi_probe::models::library_parent_dto::Kind::Item
    ));
    assert_eq!(decoded.parent.item_id.to_string(), "88888888-8888-8888-8888-888888888888");
    assert_eq!(decoded.name, "logo.svg");
    assert_eq!(serde_json::to_value(decoded).unwrap(), slot);

    let read_request = json!({
        "ItemId":"aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
        "ContentVersionId":"bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb",
        "ContentRevision":"cursor-revision-3"
    });
    let decoded: PrepareLibraryContentReadParameters = serde_json::from_value(read_request.clone()).unwrap();
    assert_eq!(decoded.content_revision, "cursor-revision-3");
    assert_eq!(serde_json::to_value(decoded).unwrap(), read_request);
}
