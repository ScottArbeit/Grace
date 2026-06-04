# PromotionSetApprovalSummary

## Properties

Name | Type | Description | Notes
------------ | ------------- | ------------- | -------------
**class** | Option<**String**> |  | [optional]
**promotion_set_id** | Option<**uuid::Uuid**> |  | [optional]
**target_branch_id** | Option<**uuid::Uuid**> |  | [optional]
**steps_computation_attempt** | Option<**i32**> |  | [optional]
**state** | Option<[**models::PromotionSetApprovalState**](PromotionSetApprovalState.md)> |  | [optional]
**approval_request_id** | Option<**uuid::Uuid**> |  | [optional]
**approval_policy_id** | Option<**uuid::Uuid**> |  | [optional]
**required_responder** | Option<**String**> |  | [optional]
**last_decision_at** | Option<**chrono::DateTime<chrono::FixedOffset>**> |  | [optional]
**expires_at** | Option<**chrono::DateTime<chrono::FixedOffset>**> |  | [optional]
**reason** | Option<**String**> |  | [optional]

[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


