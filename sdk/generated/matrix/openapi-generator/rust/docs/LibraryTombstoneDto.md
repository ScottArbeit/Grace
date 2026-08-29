# LibraryTombstoneDto

## Properties

Name | Type | Description | Notes
------------ | ------------- | ------------- | -------------
**item_id** | **uuid::Uuid** |  | 
**item_kind** | [**models::LibraryItemKind**](LibraryItemKind.md) |  | 
**deleted_at** | **chrono::DateTime<chrono::FixedOffset>** |  | 
**deleted_by** | **String** |  | 
**delete_cursor** | **String** | Opaque repository cursor. Clients must not parse or compare its contents. | 
**last_namespace_version** | **uuid::Uuid** |  | 
**last_content_version_id** | **uuid::Uuid** |  | 

[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


