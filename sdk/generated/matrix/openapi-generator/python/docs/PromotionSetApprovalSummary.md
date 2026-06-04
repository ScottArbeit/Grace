# PromotionSetApprovalSummary


## Properties

Name | Type | Description | Notes
------------ | ------------- | ------------- | -------------
**var_class** | **str** |  | [optional] 
**promotion_set_id** | **UUID** |  | [optional] 
**target_branch_id** | **UUID** |  | [optional] 
**steps_computation_attempt** | **int** |  | [optional] 
**state** | [**PromotionSetApprovalState**](PromotionSetApprovalState.md) |  | [optional] 
**approval_request_id** | **UUID** |  | [optional] 
**approval_policy_id** | **UUID** |  | [optional] 
**required_responder** | **str** |  | [optional] 
**last_decision_at** | **datetime** |  | [optional] 
**expires_at** | **datetime** |  | [optional] 
**reason** | **str** |  | [optional] 

## Example

```python
from grace_generated_openapi_probe.models.promotion_set_approval_summary import PromotionSetApprovalSummary

# TODO update the JSON string below
json = "{}"
# create an instance of PromotionSetApprovalSummary from a JSON string
promotion_set_approval_summary_instance = PromotionSetApprovalSummary.from_json(json)
# print the JSON string representation of the object
print(PromotionSetApprovalSummary.to_json())

# convert the object into a dict
promotion_set_approval_summary_dict = promotion_set_approval_summary_instance.to_dict()
# create an instance of PromotionSetApprovalSummary from a dict
promotion_set_approval_summary_from_dict = PromotionSetApprovalSummary.from_dict(promotion_set_approval_summary_dict)
```
[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


