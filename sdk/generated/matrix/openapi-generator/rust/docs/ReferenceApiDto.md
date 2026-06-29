# ReferenceApiDto

## Properties

Name | Type | Description | Notes
------------ | ------------- | ------------- | -------------
**class** | Option<**String**> |  | [optional]
**reference_id** | Option<**uuid::Uuid**> |  | [optional]
**owner_id** | Option<**uuid::Uuid**> |  | [optional]
**organization_id** | Option<**uuid::Uuid**> |  | [optional]
**repository_id** | Option<**uuid::Uuid**> |  | [optional]
**branch_id** | Option<**uuid::Uuid**> |  | [optional]
**directory_id** | Option<**uuid::Uuid**> | DirectoryVersionId represented by the current server DTO field name. | [optional]
**sha256_hash** | Option<**String**> | Empty value or lowercase 64-character SHA-256 hash for legacy or default reference DTOs. | [optional]
**blake3_hash** | Option<**String**> | Empty value or lowercase 64-character BLAKE3 hash for legacy reference DTOs. | [optional]
**reference_type** | Option<[**models::ReferenceType**](ReferenceType.md)> |  | [optional]
**reference_text** | Option<**String**> |  | [optional]
**links** | Option<**Vec<String>**> |  | [optional]
**created_at** | Option<**chrono::DateTime<chrono::FixedOffset>**> |  | [optional]
**updated_at** | Option<**chrono::DateTime<chrono::FixedOffset>**> |  | [optional]
**deleted_at** | Option<**chrono::DateTime<chrono::FixedOffset>**> |  | [optional]
**delete_reason** | Option<**String**> |  | [optional]

[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


