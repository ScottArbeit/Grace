# StartManifestUploadSessionParameters

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
**file_content_hash** | Option<**String**> | Lowercase 64-character BLAKE3 hash of the complete unencoded file bytes. | [optional]
**expected_size** | Option<**i64**> |  | [optional]
**chunking_suite_id** | Option<**String**> | Versioned chunking suite identifier. | [optional]
**sampling_policy_snapshot** | Option<**String**> |  | [optional]
**operation_id** | Option<**String**> | Caller-supplied idempotency key for one upload-session operation. | [optional]

[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


