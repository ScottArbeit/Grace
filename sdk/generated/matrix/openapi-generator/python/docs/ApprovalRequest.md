# ApprovalRequest


## Properties

Name | Type | Description | Notes
------------ | ------------- | ------------- | -------------
**var_class** | **str** |  | [optional] 
**approval_request_id** | **UUID** |  | [optional] 
**approval_policy_id** | **UUID** |  | [optional] 
**approval_policy_version** | **int** |  | [optional] 
**subject** | **str** |  | [optional] 
**scope** | [**ApprovalScope**](ApprovalScope.md) |  | [optional] 
**required_responder** | **str** |  | [optional] 
**status** | [**ApprovalRequestStatus**](ApprovalRequestStatus.md) |  | [optional] 
**decision** | [**ApprovalRequestDecision**](ApprovalRequestDecision.md) |  | [optional] 
**created_by** | **str** |  | [optional] 
**created_at** | **datetime** |  | [optional] 
**expires_at** | **datetime** |  | [optional] 
**updated_at** | **datetime** |  | [optional] 
**superseded_by_approval_request_id** | **UUID** |  | [optional] 

## Example

```python
from grace_generated_openapi_probe.models.approval_request import ApprovalRequest

# TODO update the JSON string below
json = "{}"
# create an instance of ApprovalRequest from a JSON string
approval_request_instance = ApprovalRequest.from_json(json)
# print the JSON string representation of the object
print(ApprovalRequest.to_json())

# convert the object into a dict
approval_request_dict = approval_request_instance.to_dict()
# create an instance of ApprovalRequest from a dict
approval_request_from_dict = ApprovalRequest.from_dict(approval_request_dict)
```
[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


