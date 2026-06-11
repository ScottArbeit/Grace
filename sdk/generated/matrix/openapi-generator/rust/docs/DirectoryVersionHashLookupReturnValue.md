# DirectoryVersionHashLookupReturnValue

## Properties

Name | Type | Description | Notes
------------ | ------------- | ------------- | -------------
**return_value** | [**models::DirectoryVersion**](DirectoryVersion.md) |  | 
**event_time** | **chrono::DateTime<chrono::FixedOffset>** |  | 
**correlation_id** | **String** | Body DTO correlation id copied into Grace command/event metadata after request parsing. This field is distinct from the X-Correlation-Id transport header, which correlates the HTTP request/response exchange. | 
**properties** | **std::collections::HashMap<String, String>** |  | 

[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


