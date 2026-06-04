# ConfirmContentBlockUploadParameters

## Properties

Name | Type | Description | Notes
------------ | ------------- | ------------- | -------------
**correlation_id** | Option<**String**> | Body DTO correlation id copied into Grace command/event metadata after request parsing. This field is distinct from the X-Correlation-Id transport header. | [optional]
**principal** | Option<**String**> | The entity on whose behalf the action is being performed. | [optional]
**owner_id** | Option<**String**> |  | [optional]
**owner_name** | Option<**String**> |  | [optional]
**organization_id** | Option<**String**> |  | [optional]
**organization_name** | Option<**String**> |  | [optional]
**repository_id** | Option<**String**> |  | [optional]
**repository_name** | Option<**String**> |  | [optional]
**upload_session_id** | Option<**uuid::Uuid**> |  | [optional]
**authorized_scope** | Option<**String**> |  | [optional]
**operation_id** | Option<**String**> | Caller-supplied idempotency key for one upload-session operation. | [optional]
**content_block_address** | Option<**String**> | Lowercase 64-character BLAKE3-derived ContentBlock address. | [optional]
**payload** | Option<**String**> |  | [optional]
**storage_placement** | Option<[**models::ContentBlockStoragePlacement**](ContentBlockStoragePlacement.md)> |  | [optional]

[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


