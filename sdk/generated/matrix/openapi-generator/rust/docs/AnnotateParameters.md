# AnnotateParameters

## Properties

Name | Type | Description | Notes
------------ | ------------- | ------------- | -------------
**correlation_id** | Option<**String**> | Body DTO correlation id copied into Grace command/event metadata after request parsing. This field is distinct from the X-Correlation-Id transport header. | [optional]
**principal** | Option<**String**> | The entity on whose behalf the action is being performed. | [optional]
**properties** | Option<**std::collections::HashMap<String, String>**> | Allow-listed event properties. UploadSessionIds is the only client-settable key. | [optional]
**owner_id** | Option<**uuid::Uuid**> |  | [optional]
**owner_name** | Option<**String**> |  | [optional]
**organization_id** | Option<**uuid::Uuid**> |  | [optional]
**organization_name** | Option<**String**> |  | [optional]
**repository_id** | Option<**uuid::Uuid**> |  | [optional]
**repository_name** | Option<**String**> |  | [optional]
**branch_id** | Option<**uuid::Uuid**> |  | [optional]
**branch_name** | Option<**String**> |  | [optional]
**target_reference_id** | Option<**uuid::Uuid**> |  | [optional]
**path** | Option<**String**> |  | [optional]
**start_line** | Option<**i32**> |  | [optional]
**end_line** | Option<**i32**> |  | [optional]
**reference_types** | Option<[**Vec<models::ReferenceType>**](ReferenceType.md)> |  | [optional]
**max_references** | Option<**i32**> |  | [optional]
**include_line_text** | Option<**bool**> |  | [optional]

[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


