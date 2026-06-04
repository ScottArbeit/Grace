# ApprovalRequest

## Properties

Name | Type | Description | Notes
------------ | ------------- | ------------- | -------------
**class** | Option<**String**> |  | [optional]
**approval_request_id** | Option<**uuid::Uuid**> |  | [optional]
**approval_policy_id** | Option<**uuid::Uuid**> |  | [optional]
**approval_policy_version** | Option<**i32**> |  | [optional]
**subject** | Option<**String**> |  | [optional]
**scope** | Option<[**models::ApprovalScope**](ApprovalScope.md)> |  | [optional]
**required_responder** | Option<**String**> |  | [optional]
**status** | Option<[**models::ApprovalRequestStatus**](ApprovalRequestStatus.md)> |  | [optional]
**decision** | Option<[**models::ApprovalRequestDecision**](ApprovalRequestDecision.md)> |  | [optional]
**created_by** | Option<**String**> |  | [optional]
**created_at** | Option<**chrono::DateTime<chrono::FixedOffset>**> |  | [optional]
**expires_at** | Option<**chrono::DateTime<chrono::FixedOffset>**> |  | [optional]
**updated_at** | Option<**chrono::DateTime<chrono::FixedOffset>**> |  | [optional]
**superseded_by_approval_request_id** | Option<**uuid::Uuid**> |  | [optional]

[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


