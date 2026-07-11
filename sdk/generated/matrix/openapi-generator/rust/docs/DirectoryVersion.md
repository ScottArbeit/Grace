# DirectoryVersion

## Properties

Name | Type | Description | Notes
------------ | ------------- | ------------- | -------------
**class** | **String** |  | 
**directory_version_id** | **uuid::Uuid** |  | 
**owner_id** | **uuid::Uuid** |  | 
**organization_id** | **uuid::Uuid** |  | 
**repository_id** | **uuid::Uuid** |  | 
**relative_path** | **String** |  | 
**sha256_hash** | **String** | Lowercase 64-character SHA-256 version hash persisted on version DTOs. | 
**blake3_hash** | **String** | Lowercase 64-character BLAKE3 version hash persisted on new version graph DTOs. | 
**directories** | **Vec<uuid::Uuid>** |  | 
**files** | [**Vec<models::FileVersion>**](FileVersion.md) |  | 
**size** | **i64** |  | 
**recursive_size** | **i64** |  | 
**created_at** | **chrono::DateTime<chrono::FixedOffset>** |  | 
**hashes_validated** | **bool** |  | 

[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


