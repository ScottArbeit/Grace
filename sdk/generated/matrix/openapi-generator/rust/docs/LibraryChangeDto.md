# LibraryChangeDto

## Properties

Name | Type | Description | Notes
------------ | ------------- | ------------- | -------------
**operation_id** | **uuid::Uuid** |  | 
**change_kind** | [**models::LibraryChangeKind**](LibraryChangeKind.md) |  | 
**accepted_at** | **chrono::DateTime<chrono::FixedOffset>** |  | 
**accepted_by** | **String** |  | 
**library_catalog_version** | **uuid::Uuid** |  | 
**item** | [**models::LibraryItemDto**](LibraryItemDto.md) |  | 
**conflict** | [**models::LibraryConflictProvenanceDto**](LibraryConflictProvenanceDto.md) |  | 

[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


