# CreateApprovalPolicyParameters

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
**target_branch_id** | Option<**uuid::Uuid**> |  | [optional]
**approval_policy_id** | Option<**uuid::Uuid**> |  | [optional]
**name** | Option<**String**> |  | [optional]
**subject** | Option<**String**> |  | [optional]
**required_responder** | Option<**String**> |  | [optional]
**notification_url** | Option<**String**> |  | [optional]
**notification_url_safety** | Option<[**models::OutboundUrlSafety**](OutboundUrlSafety.md)> |  | [optional]
**acknowledge_unsafe_local_development** | Option<**bool**> |  | [optional][default to false]
**timeout_seconds** | Option<**i32**> |  | [optional]
**on_timeout** | Option<[**models::ApprovalTimeoutAction**](ApprovalTimeoutAction.md)> |  | [optional]

[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


