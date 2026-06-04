# ApprovalRequestDecision


## Properties

Name | Type | Description | Notes
------------ | ------------- | ------------- | -------------
**decision** | [**ApprovalDecision**](ApprovalDecision.md) |  | [optional] 
**decided_by** | **str** |  | [optional] 
**decided_at** | **datetime** |  | [optional] 
**reason** | **str** |  | [optional] 
**client_decision_id** | **str** |  | [optional] 

## Example

```python
from grace_generated_openapi_probe.models.approval_request_decision import ApprovalRequestDecision

# TODO update the JSON string below
json = "{}"
# create an instance of ApprovalRequestDecision from a JSON string
approval_request_decision_instance = ApprovalRequestDecision.from_json(json)
# print the JSON string representation of the object
print(ApprovalRequestDecision.to_json())

# convert the object into a dict
approval_request_decision_dict = approval_request_decision_instance.to_dict()
# create an instance of ApprovalRequestDecision from a dict
approval_request_decision_from_dict = ApprovalRequestDecision.from_dict(approval_request_decision_dict)
```
[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


