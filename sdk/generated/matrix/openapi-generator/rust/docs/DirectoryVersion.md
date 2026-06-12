# DirectoryVersion

## Properties

Name | Type | Description | Notes
------------ | ------------- | ------------- | -------------
**class** | Option<**String**> |  | [optional]
**directory_version_id** | Option<**uuid::Uuid**> |  | [optional]
**owner_id** | Option<**uuid::Uuid**> |  | [optional]
**organization_id** | Option<**uuid::Uuid**> |  | [optional]
**repository_id** | Option<**uuid::Uuid**> |  | [optional]
**relative_path** | Option<**String**> |  | [optional]
**sha256_hash** | Option<**String**> | Lowercase 64-character SHA-256 version hash persisted on version DTOs. | [optional]
**blake3_hash** | Option<**String**> | Empty value, null, or lowercase 64-character BLAKE3 version hash for legacy version DTOs. | [optional]
**directories** | Option<**Vec<uuid::Uuid>**> |  | [optional]
**files** | Option<[**Vec<models::FileVersion>**](FileVersion.md)> |  | [optional]
**size** | Option<**i64**> |  | [optional]
**recursive_size** | Option<**i64**> |  | [optional]
**created_at** | Option<**chrono::DateTime<chrono::FixedOffset>**> |  | [optional]
**hashes_validated** | Option<**bool**> |  | [optional]

[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


