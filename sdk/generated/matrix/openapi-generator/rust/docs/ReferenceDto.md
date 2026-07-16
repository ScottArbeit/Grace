# ReferenceDto

## Properties

Name | Type | Description | Notes
------------ | ------------- | ------------- | -------------
**class** | **String** |  | 
**reference_id** | **uuid::Uuid** |  | 
**branch_id** | **uuid::Uuid** |  | 
**directory_id** | **uuid::Uuid** |  | 
**sha256_hash** | **String** | Lowercase 64-character SHA-256 version hash persisted on version DTOs. | 
**blake3_hash** | **String** | Lowercase 64-character BLAKE3 version hash persisted on new version graph DTOs. | 
**reference_type** | [**models::ReferenceType**](ReferenceType.md) |  | 
**reference_text** | **String** |  | 
**created_by** | Option<**String**> |  | [optional]
**created_at** | **chrono::DateTime<chrono::FixedOffset>** |  | 

[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


