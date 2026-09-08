# LibraryItemDto

## Properties

Name | Type | Description | Notes
------------ | ------------- | ------------- | -------------
**item_id** | **uuid::Uuid** |  | 
**item_kind** | [**models::LibraryItemKind**](LibraryItemKind.md) |  | 
**last_change_cursor** | **String** | Opaque repository cursor. Clients must not parse or compare its contents. | 
**namespace** | [**models::LibraryNamespaceDto**](LibraryNamespaceDto.md) |  | 
**content** | [**models::LibraryContentVersionDto**](LibraryContentVersionDto.md) |  | 
**content_revision** | **String** | Opaque repository cursor. Clients must not parse or compare its contents. | 
**tombstone** | [**models::LibraryTombstoneDto**](LibraryTombstoneDto.md) |  | 

[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


