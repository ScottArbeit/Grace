# ApprovalScope


## Properties

Name | Type | Description | Notes
------------ | ------------- | ------------- | -------------
**owner_id** | **UUID** |  | [optional] 
**organization_id** | **UUID** |  | [optional] 
**repository_id** | **UUID** |  | [optional] 
**target_branch_id** | **UUID** |  | [optional] 
**promotion_set_id** | **UUID** |  | [optional] 
**steps_computation_attempt** | **int** |  | [optional] 
**approval_policy_id** | **UUID** |  | [optional] 
**approval_policy_version** | **int** |  | [optional] 

## Example

```python
from grace_generated_openapi_probe.models.approval_scope import ApprovalScope

# TODO update the JSON string below
json = "{}"
# create an instance of ApprovalScope from a JSON string
approval_scope_instance = ApprovalScope.from_json(json)
# print the JSON string representation of the object
print(ApprovalScope.to_json())

# convert the object into a dict
approval_scope_dict = approval_scope_instance.to_dict()
# create an instance of ApprovalScope from a dict
approval_scope_from_dict = ApprovalScope.from_dict(approval_scope_dict)
```
[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


