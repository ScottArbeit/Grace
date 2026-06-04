# EvaluateApprovalPolicyParameters


## Properties

Name | Type | Description | Notes
------------ | ------------- | ------------- | -------------
**correlation_id** | **str** | Body DTO correlation id copied into Grace command/event metadata after request parsing. This field is distinct from the X-Correlation-Id transport header. | [optional] 
**principal** | **str** | The entity on whose behalf the action is being performed. | [optional] 
**owner_id** | **UUID** |  | [optional] 
**owner_name** | **str** |  | [optional] 
**organization_id** | **UUID** |  | [optional] 
**organization_name** | **str** |  | [optional] 
**repository_id** | **UUID** |  | [optional] 
**repository_name** | **str** |  | [optional] 
**target_branch_id** | **UUID** |  | [optional] 
**approval_policy_id** | **UUID** |  | [optional] 
**subject** | **str** |  | [optional] 

## Example

```python
from grace_generated_openapi_probe.models.evaluate_approval_policy_parameters import EvaluateApprovalPolicyParameters

# TODO update the JSON string below
json = "{}"
# create an instance of EvaluateApprovalPolicyParameters from a JSON string
evaluate_approval_policy_parameters_instance = EvaluateApprovalPolicyParameters.from_json(json)
# print the JSON string representation of the object
print(EvaluateApprovalPolicyParameters.to_json())

# convert the object into a dict
evaluate_approval_policy_parameters_dict = evaluate_approval_policy_parameters_instance.to_dict()
# create an instance of EvaluateApprovalPolicyParameters from a dict
evaluate_approval_policy_parameters_from_dict = EvaluateApprovalPolicyParameters.from_dict(evaluate_approval_policy_parameters_dict)
```
[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


