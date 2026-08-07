# CurrentBranchReferenceNotification

## Properties

Name | Type | Description | Notes
------------ | ------------- | ------------- | -------------
**reference_id** | **uuid::Uuid** |  | 
**owner_id** | **uuid::Uuid** |  | 
**organization_id** | **uuid::Uuid** |  | 
**repository_id** | **uuid::Uuid** |  | 
**branch_id** | **uuid::Uuid** |  | 
**branch_name** | **String** |  | 
**directory_id** | **uuid::Uuid** |  | 
**sha256_hash** | **String** | Lowercase 64-character SHA-256 version hash persisted on version DTOs. | 
**blake3_hash** | **String** | Lowercase 64-character BLAKE3 version hash persisted on new version graph DTOs. | 
**reference_type** | [**models::ReferenceType**](ReferenceType.md) |  | 
**reference_text** | **String** |  | 
**correlation_id** | **String** | Body DTO correlation id copied into Grace command/event metadata after request parsing. This field is distinct from the X-Correlation-Id transport header, which correlates the HTTP request/response exchange. | 

[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


