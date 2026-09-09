# LibraryContentPreparationDto

## Properties

Name | Type | Description | Notes
------------ | ------------- | ------------- | -------------
**upload_session_id** | **uuid::Uuid** |  | 
**blake3_hash** | **String** | Lowercase 64-character BLAKE3 version hash persisted on new version graph DTOs. | 
**sha256_hash** | **String** | Lowercase 64-character SHA-256 version hash persisted on version DTOs. | 
**size** | **i64** |  | 
**authorized_scope** | **String** |  | 
**storage_pool_id** | **String** |  | 
**expires_at** | **chrono::DateTime<chrono::FixedOffset>** |  | 

[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


