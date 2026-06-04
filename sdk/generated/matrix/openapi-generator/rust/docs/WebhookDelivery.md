# WebhookDelivery

## Properties

Name | Type | Description | Notes
------------ | ------------- | ------------- | -------------
**class** | Option<**String**> |  | [optional]
**webhook_delivery_id** | Option<**uuid::Uuid**> |  | [optional]
**webhook_rule_id** | Option<**uuid::Uuid**> |  | [optional]
**event_name** | Option<**String**> |  | [optional]
**event_version** | Option<**i32**> |  | [optional]
**dedupe_key** | Option<**String**> |  | [optional]
**status** | Option<[**models::WebhookDeliveryStatus**](WebhookDeliveryStatus.md)> |  | [optional]
**attempt_count** | Option<**i32**> |  | [optional]
**last_attempt_at** | Option<**chrono::DateTime<chrono::FixedOffset>**> |  | [optional]
**next_attempt_at** | Option<**chrono::DateTime<chrono::FixedOffset>**> |  | [optional]
**last_status_code** | Option<**i32**> |  | [optional]
**last_error** | Option<**String**> |  | [optional]
**created_at** | Option<**chrono::DateTime<chrono::FixedOffset>**> |  | [optional]

[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


