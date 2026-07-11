# MaterializationPlan

## Properties

Name | Type | Description | Notes
------------ | ------------- | ------------- | -------------
**class** | **String** |  | 
**target_root_directory_version_id** | **uuid::Uuid** |  | 
**execution_mode** | [**models::MaterializationExecutionMode**](MaterializationExecutionMode.md) |  | 
**cache_selection** | [**models::MaterializationCacheSelection**](MaterializationCacheSelection.md) |  | 
**required_artifacts** | [**Vec<models::MaterializationArtifactDescriptor>**](MaterializationArtifactDescriptor.md) |  | 
**artifact_grant** | Option<[**models::SignedArtifactGrant**](SignedArtifactGrant.md)> |  | [optional]

[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


