# CreateWebhookRuleParameters

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
**webhook_rule_id** | Option<**uuid::Uuid**> |  | [optional]
**name** | Option<**String**> |  | [optional]
**event_name** | Option<**String**> |  | [optional]
**event_version** | Option<**i32**> |  | [optional][default to 1]
**url** | Option<**String**> |  | [optional]
**url_safety** | Option<[**models::OutboundUrlSafety**](OutboundUrlSafety.md)> |  | [optional]
**acknowledge_unsafe_local_development** | Option<**bool**> |  | [optional][default to false]
**signing_secret_version** | Option<**String**> |  | [optional]
**max_attempts** | Option<**i32**> |  | [optional][default to 8]
**initial_delay_seconds** | Option<**i32**> |  | [optional][default to 30]
**max_delay_seconds** | Option<**i32**> |  | [optional][default to 3600]

[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


