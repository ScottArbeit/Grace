# SynchronizedItemDto

## Properties

Name | Type | Description | Notes
------------ | ------------- | ------------- | -------------
**item_id** | **uuid::Uuid** |  | 
**item_kind** | [**models::SynchronizedItemKind**](SynchronizedItemKind.md) |  | 
**state** | **State** |  (enum: live, tombstoned) | 
**last_mutation_cursor** | **String** | Opaque repository cursor. Clients must not parse or compare its contents. | 
**root_configuration_version** | **uuid::Uuid** |  | 
**namespace** | [**models::SynchronizedNamespaceDto**](SynchronizedNamespaceDto.md) |  | 
**content** | [**models::SynchronizedContentVersionDto**](SynchronizedContentVersionDto.md) |  | 
**tombstone** | [**models::SynchronizedTombstoneDto**](SynchronizedTombstoneDto.md) |  | 

[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


