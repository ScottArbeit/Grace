# SynchronizedOperationReceiptDto

## Properties

Name | Type | Description | Notes
------------ | ------------- | ------------- | -------------
**operation_id** | **uuid::Uuid** |  | 
**request_hash** | **String** |  | 
**outcome** | [**models::SynchronizedOutcomeKind**](SynchronizedOutcomeKind.md) |  | 
**root_configuration_version** | **uuid::Uuid** |  | 
**recorded_at** | **chrono::DateTime<chrono::FixedOffset>** |  | 
**principal_id** | **String** |  | 
**mutation** | [**models::SynchronizedMutationDto**](SynchronizedMutationDto.md) |  | 
**cursor** | **String** | Opaque repository cursor. Clients must not parse or compare its contents. | 
**item** | [**models::SynchronizedItemDto**](SynchronizedItemDto.md) |  | 
**conflict** | [**models::SynchronizedConflictProvenanceDto**](SynchronizedConflictProvenanceDto.md) |  | 
**reason_code** | **String** |  | 
**current_root_configuration** | [**models::SynchronizedRootConfigurationDto**](SynchronizedRootConfigurationDto.md) |  | 
**rebaseline** | [**models::SynchronizedRebaselineDto**](SynchronizedRebaselineDto.md) |  | 

[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


