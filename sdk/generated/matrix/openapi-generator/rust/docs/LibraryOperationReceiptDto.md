# LibraryOperationReceiptDto

## Properties

Name | Type | Description | Notes
------------ | ------------- | ------------- | -------------
**operation_id** | **uuid::Uuid** |  | 
**request_hash** | **String** |  | 
**outcome** | [**models::LibraryOutcomeKind**](LibraryOutcomeKind.md) |  | 
**library_catalog_version** | **uuid::Uuid** |  | 
**recorded_at** | **chrono::DateTime<chrono::FixedOffset>** |  | 
**principal_id** | **String** |  | 
**change** | [**models::LibraryChangeDto**](LibraryChangeDto.md) |  | 
**cursor** | **String** | Opaque repository cursor. Clients must not parse or compare its contents. | 
**item** | [**models::LibraryItemDto**](LibraryItemDto.md) |  | 
**conflict** | [**models::LibraryConflictProvenanceDto**](LibraryConflictProvenanceDto.md) |  | 
**reason_code** | **String** |  | 
**current_library_catalog** | [**models::LibraryCatalogDto**](LibraryCatalogDto.md) |  | 
**rebaseline** | [**models::LibraryRebaselineDto**](LibraryRebaselineDto.md) |  | 

[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


