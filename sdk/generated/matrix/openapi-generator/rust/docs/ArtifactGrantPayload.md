# ArtifactGrantPayload

## Properties

Name | Type | Description | Notes
------------ | ------------- | ------------- | -------------
**class** | **String** |  | 
**issuer** | **String** |  | 
**requester_principal_type** | [**models::ArtifactGrantRequesterPrincipalType**](ArtifactGrantRequesterPrincipalType.md) |  | 
**requester_principal_id** | **String** | Stable authenticated grace_user_id supplied by Grace Server. | 
**holder_key_thumbprint** | **String** | Base64url SHA-256 thumbprint of the canonical ephemeral holder public key. | 
**cache_id** | **String** |  | 
**cache_endpoint** | **String** | Exact selected Cache endpoint covered by this signature. | 
**target_root_directory_version_id** | **uuid::Uuid** |  | 
**execution_mode** | [**models::MaterializationExecutionMode**](MaterializationExecutionMode.md) |  | 
**artifact_identities** | **Vec<String>** |  | 
**issued_at** | **chrono::DateTime<chrono::FixedOffset>** |  | 
**not_before** | **chrono::DateTime<chrono::FixedOffset>** |  | 
**expires_at** | **chrono::DateTime<chrono::FixedOffset>** |  | 

[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


