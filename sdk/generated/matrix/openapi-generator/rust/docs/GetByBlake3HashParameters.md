# GetByBlake3HashParameters

## Properties

Name | Type | Description | Notes
------------ | ------------- | ------------- | -------------
**correlation_id** | Option<**String**> | Body DTO correlation id copied into Grace command/event metadata after request parsing. This field is distinct from the X-Correlation-Id transport header. | [optional]
**principal** | Option<**String**> | The entity on whose behalf the action is being performed. | [optional]
**owner_id** | Option<**uuid::Uuid**> |  | [optional]
**owner_name** | Option<**String**> |  | [optional]
**organization_id** | Option<**uuid::Uuid**> |  | [optional]
**organization_name** | Option<**String**> |  | [optional]
**repository_id** | Option<**uuid::Uuid**> |  | [optional]
**repository_name** | Option<**String**> |  | [optional]
**directory_version_id** | Option<**uuid::Uuid**> |  | [optional]
**blake3_hash** | Option<**String**> | Lowercase or uppercase 2- to 64-character BLAKE3 version hash prefix used for version graph lookups. | [optional]

[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


