# LibraryChangeDto

## Properties

Name | Type | Description | Notes
------------ | ------------- | ------------- | -------------
**cursor** | **String** | Opaque repository cursor. Clients must not parse or compare its contents. | 
**operation_id** | **uuid::Uuid** |  | 
**change_kind** | [**models::LibraryChangeKind**](LibraryChangeKind.md) |  | 
**item_id** | **uuid::Uuid** |  | 
**item_kind** | [**models::LibraryItemKind**](LibraryItemKind.md) |  | 
**accepted_at** | **chrono::DateTime<chrono::FixedOffset>** |  | 
**accepted_by** | **String** |  | 
**library_catalog_version** | **uuid::Uuid** |  | 
**namespace** | [**models::LibraryNamespaceDto**](LibraryNamespaceDto.md) |  | 
**content** | [**models::LibraryContentVersionDto**](LibraryContentVersionDto.md) |  | 
**tombstone** | [**models::LibraryTombstoneDto**](LibraryTombstoneDto.md) |  | 
**conflict** | [**models::LibraryConflictProvenanceDto**](LibraryConflictProvenanceDto.md) |  | 

[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


