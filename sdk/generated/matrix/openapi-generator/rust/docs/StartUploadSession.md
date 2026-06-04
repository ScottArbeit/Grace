# StartUploadSession

## Properties

Name | Type | Description | Notes
------------ | ------------- | ------------- | -------------
**upload_session_id** | **uuid::Uuid** |  | 
**owner_id** | **uuid::Uuid** |  | 
**organization_id** | **uuid::Uuid** |  | 
**repository_id** | **uuid::Uuid** |  | 
**authorized_scope** | **String** |  | 
**file_content_hash** | **String** | Lowercase 64-character BLAKE3 hash of the complete unencoded file bytes. | 
**expected_size** | **i64** |  | 
**chunking_suite_id** | **String** | Versioned chunking suite identifier. | 
**sampling_policy_snapshot** | **String** |  | 
**operation_id** | **String** | Caller-supplied idempotency key for one upload-session operation. | 

[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


