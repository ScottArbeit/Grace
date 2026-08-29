# LibraryContentAvailable

## Properties

Name | Type | Description | Notes
------------ | ------------- | ------------- | -------------
**event_name** | **EventName** |  (enum: LibraryContentAvailable.v1) | 
**repository_id** | **uuid::Uuid** |  | 
**cursor_epoch** | **String** | Opaque repository epoch. Clients compare only exact equality. | 
**available_after_cursor** | **String** | Opaque repository cursor. Clients must not parse or compare its contents. | 
**library_catalog_version** | **uuid::Uuid** |  | 
**occurred_at** | **chrono::DateTime<chrono::FixedOffset>** |  | 
**correlation_id** | **String** | Body DTO correlation id copied into Grace command/event metadata after request parsing. This field is distinct from the X-Correlation-Id transport header, which correlates the HTTP request/response exchange. | 

[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


