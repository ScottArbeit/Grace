# LibraryChangePageDto

## Properties

Name | Type | Description | Notes
------------ | ------------- | ------------- | -------------
**outcome** | [**models::LibraryOutcomeKind**](LibraryOutcomeKind.md) |  | 
**cursor_epoch** | **String** | Opaque repository epoch. Clients compare only exact equality. | 
**changes** | [**Vec<models::LibraryChangeDto>**](LibraryChangeDto.md) |  | 
**last_cursor** | **String** | Opaque repository cursor. Clients must not parse or compare its contents. | 
**has_more** | **bool** |  | 
**next_page_token** | **String** | Opaque token for continuing one immutable page sequence. | 
**rebaseline** | [**models::LibraryRebaselineDto**](LibraryRebaselineDto.md) |  | 

[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


