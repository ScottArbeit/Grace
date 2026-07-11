# SetOwnerNameParameters

## Properties

Name | Type | Description | Notes
------------ | ------------- | ------------- | -------------
**correlation_id** | Option<**String**> | Body DTO correlation id copied into Grace command/event metadata after request parsing. This field is distinct from the X-Correlation-Id transport header. | [optional]
**principal** | Option<**String**> | The entity on whose behalf the action is being performed. | [optional]
**properties** | Option<**std::collections::HashMap<String, String>**> | Allow-listed event properties. UploadSessionIds is the only client-settable key. | [optional]
**owner_id** | Option<**uuid::Uuid**> |  | [optional]
**owner_name** | Option<**String**> |  | [optional]
**new_name** | Option<**String**> |  | [optional]

[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


