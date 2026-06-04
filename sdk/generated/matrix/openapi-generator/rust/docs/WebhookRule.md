# WebhookRule

## Properties

Name | Type | Description | Notes
------------ | ------------- | ------------- | -------------
**class** | Option<**String**> |  | [optional]
**webhook_rule_id** | Option<**uuid::Uuid**> |  | [optional]
**name** | Option<**String**> |  | [optional]
**event_name** | Option<**String**> |  | [optional]
**event_version** | Option<**i32**> |  | [optional]
**scope** | Option<[**models::WebhookScope**](WebhookScope.md)> |  | [optional]
**url** | Option<[**models::ScopedOutboundUrl**](ScopedOutboundUrl.md)> |  | [optional]
**signing_secret_version** | Option<**String**> |  | [optional]
**retry_policy** | Option<[**models::WebhookRetryPolicy**](WebhookRetryPolicy.md)> |  | [optional]
**status** | Option<[**models::WebhookRuleStatus**](WebhookRuleStatus.md)> |  | [optional]
**created_by** | Option<**String**> |  | [optional]
**created_at** | Option<**chrono::DateTime<chrono::FixedOffset>**> |  | [optional]
**updated_at** | Option<**chrono::DateTime<chrono::FixedOffset>**> |  | [optional]

[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


