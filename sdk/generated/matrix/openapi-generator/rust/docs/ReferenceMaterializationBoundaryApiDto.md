# ReferenceMaterializationBoundaryApiDto

## Properties

Name | Type | Description | Notes
------------ | ------------- | ------------- | -------------
**repository_id** | **uuid::Uuid** |  | 
**branch_id** | **uuid::Uuid** |  | 
**directory_id** | **uuid::Uuid** |  | 
**sha256_hash** | **String** | Lowercase 64-character SHA-256 version hash persisted on version DTOs. | 
**blake3_hash** | **String** | Lowercase 64-character BLAKE3 version hash persisted on new version graph DTOs. | 
**event_cursor** | **String** | Opaque cursor interpreted only by Grace Server. | 

[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


