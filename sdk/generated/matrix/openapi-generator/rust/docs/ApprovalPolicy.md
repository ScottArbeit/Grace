# ApprovalPolicy

## Properties

Name | Type | Description | Notes
------------ | ------------- | ------------- | -------------
**class** | Option<**String**> |  | [optional]
**approval_policy_id** | Option<**uuid::Uuid**> |  | [optional]
**version** | Option<**i32**> |  | [optional]
**name** | Option<**String**> |  | [optional]
**subject** | Option<**String**> |  | [optional]
**scope** | Option<[**models::ApprovalScope**](ApprovalScope.md)> |  | [optional]
**required_responder** | Option<**String**> |  | [optional]
**notification_url** | Option<[**models::ScopedOutboundUrl**](ScopedOutboundUrl.md)> |  | [optional]
**timeout_seconds** | Option<**i32**> |  | [optional]
**on_timeout** | Option<[**models::ApprovalTimeoutAction**](ApprovalTimeoutAction.md)> |  | [optional]
**status** | Option<[**models::ApprovalPolicyStatus**](ApprovalPolicyStatus.md)> |  | [optional]
**created_by** | Option<**String**> |  | [optional]
**created_at** | Option<**chrono::DateTime<chrono::FixedOffset>**> |  | [optional]
**updated_at** | Option<**chrono::DateTime<chrono::FixedOffset>**> |  | [optional]

[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


