# LibraryContentReadGrantReturnValue

## Properties

Name | Type | Description | Notes
------------ | ------------- | ------------- | -------------
**event_time** | **chrono::DateTime<chrono::FixedOffset>** |  | 
**correlation_id** | **String** | Body DTO correlation id copied into Grace command/event metadata after request parsing. This field is distinct from the X-Correlation-Id transport header, which correlates the HTTP request/response exchange. | 
**properties** | **std::collections::HashMap<String, String>** |  | 
**return_value** | Option<[**models::LibraryContentReadGrantDto**](LibraryContentReadGrantDto.md)> |  | [optional]

[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


