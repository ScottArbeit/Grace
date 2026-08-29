# LibraryPreparedContentDto

## Properties

Name | Type | Description | Notes
------------ | ------------- | ------------- | -------------
**prepared_content_id** | **uuid::Uuid** |  | 
**blake3_hash** | **String** | Lowercase 64-character BLAKE3 version hash persisted on new version graph DTOs. | 
**sha256_hash** | **String** | Lowercase 64-character SHA-256 version hash persisted on version DTOs. | 
**size** | **i64** |  | 
**upload_required** | **bool** |  | 
**upload_instructions** | **String** |  | 
**expires_at** | **chrono::DateTime<chrono::FixedOffset>** |  | 

[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


