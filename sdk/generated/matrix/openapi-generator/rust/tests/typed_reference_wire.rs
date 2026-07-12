use grace_generated_openapi_probe::models::TypedReferenceApiDto;
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
}
