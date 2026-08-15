# FileVersion

## Properties

Name | Type | Description | Notes
------------ | ------------- | ------------- | -------------
**class** | **String** |  | 
**relative_path** | **String** |  | 
**sha256_hash** | **String** | Lowercase 64-character SHA-256 version hash persisted on version DTOs. | 
**blake3_hash** | **String** | Lowercase 64-character BLAKE3 version hash persisted on new version graph DTOs. | 
**is_binary** | **bool** |  | 
**size** | **i64** |  | 
**created_at** | **chrono::DateTime<chrono::FixedOffset>** |  | 
**blob_uri** | **String** |  | 
**content_reference** | [**models::FileContentReference**](FileContentReference.md) |  | 

[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


