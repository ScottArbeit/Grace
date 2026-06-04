# DeleteBranchParameters

Parameters for the /branch/delete endpoint.

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
**branch_id** | **UUID** |  | [optional] 
**branch_name** | **str** |  | [optional] 
**force** | **bool** |  | [optional] 
**delete_reason** | **str** |  | [optional] 
**reassign_child_branches** | **bool** |  | [optional] 
**new_parent_branch_id** | **UUID** |  | [optional] 
**new_parent_branch_name** | **str** |  | [optional] 

## Example

```python
from grace_generated_openapi_probe.models.delete_branch_parameters import DeleteBranchParameters

# TODO update the JSON string below
json = "{}"
# create an instance of DeleteBranchParameters from a JSON string
delete_branch_parameters_instance = DeleteBranchParameters.from_json(json)
# print the JSON string representation of the object
print(DeleteBranchParameters.to_json())

# convert the object into a dict
delete_branch_parameters_dict = delete_branch_parameters_instance.to_dict()
# create an instance of DeleteBranchParameters from a dict
delete_branch_parameters_from_dict = DeleteBranchParameters.from_dict(delete_branch_parameters_dict)
```
[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


