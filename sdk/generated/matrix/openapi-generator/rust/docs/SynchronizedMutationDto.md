# SynchronizedMutationDto

## Properties

Name | Type | Description | Notes
------------ | ------------- | ------------- | -------------
**cursor** | **String** | Opaque repository cursor. Clients must not parse or compare its contents. | 
**operation_id** | **uuid::Uuid** |  | 
**mutation_kind** | [**models::SynchronizedMutationKind**](SynchronizedMutationKind.md) |  | 
**item_id** | **uuid::Uuid** |  | 
**item_kind** | [**models::SynchronizedItemKind**](SynchronizedItemKind.md) |  | 
**accepted_at** | **chrono::DateTime<chrono::FixedOffset>** |  | 
**accepted_by** | **String** |  | 
**root_configuration_version** | **uuid::Uuid** |  | 
**namespace** | [**models::SynchronizedNamespaceDto**](SynchronizedNamespaceDto.md) |  | 
**content** | [**models::SynchronizedContentVersionDto**](SynchronizedContentVersionDto.md) |  | 
**tombstone** | [**models::SynchronizedTombstoneDto**](SynchronizedTombstoneDto.md) |  | 
**conflict** | [**models::SynchronizedConflictProvenanceDto**](SynchronizedConflictProvenanceDto.md) |  | 

[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


