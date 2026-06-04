# ApprovalPolicy


## Properties

Name | Type | Description | Notes
------------ | ------------- | ------------- | -------------
**var_class** | **str** |  | [optional] 
**approval_policy_id** | **UUID** |  | [optional] 
**version** | **int** |  | [optional] 
**name** | **str** |  | [optional] 
**subject** | **str** |  | [optional] 
**scope** | [**ApprovalScope**](ApprovalScope.md) |  | [optional] 
**required_responder** | **str** |  | [optional] 
**notification_url** | [**ScopedOutboundUrl**](ScopedOutboundUrl.md) |  | [optional] 
**timeout_seconds** | **int** |  | [optional] 
**on_timeout** | [**ApprovalTimeoutAction**](ApprovalTimeoutAction.md) |  | [optional] 
**status** | [**ApprovalPolicyStatus**](ApprovalPolicyStatus.md) |  | [optional] 
**created_by** | **str** |  | [optional] 
**created_at** | **datetime** |  | [optional] 
**updated_at** | **datetime** |  | [optional] 

## Example

```python
from grace_generated_openapi_probe.models.approval_policy import ApprovalPolicy

# TODO update the JSON string below
json = "{}"
# create an instance of ApprovalPolicy from a JSON string
approval_policy_instance = ApprovalPolicy.from_json(json)
# print the JSON string representation of the object
print(ApprovalPolicy.to_json())

# convert the object into a dict
approval_policy_dict = approval_policy_instance.to_dict()
# create an instance of ApprovalPolicy from a dict
approval_policy_from_dict = ApprovalPolicy.from_dict(approval_policy_dict)
```
[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


