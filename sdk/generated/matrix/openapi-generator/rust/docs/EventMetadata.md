# EventMetadata

## Properties

Name | Type | Description | Notes
------------ | ------------- | ------------- | -------------
**timestamp** | Option<**chrono::DateTime<chrono::FixedOffset>**> |  | [optional]
**correlation_id** | Option<**String**> |  | [optional]
**principal** | Option<**String**> |  | [optional]
**client_type** | Option<**String**> | The Grace client identity type/version recorded for diagnostics. Client identity headers are not authentication proof and must not be used as authorization evidence. | [optional]
**properties** | Option<**std::collections::HashMap<String, String>**> |  | [optional]

[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


