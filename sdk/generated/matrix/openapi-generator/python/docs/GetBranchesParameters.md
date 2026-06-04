# GetBranchesParameters

Parameters for the /repository/getBranches endpoint.

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
**include_deleted** | **bool** |  | [optional] 
**max_count** | **int** |  | [optional] 

## Example

```python
from grace_generated_openapi_probe.models.get_branches_parameters import GetBranchesParameters

# TODO update the JSON string below
json = "{}"
# create an instance of GetBranchesParameters from a JSON string
get_branches_parameters_instance = GetBranchesParameters.from_json(json)
# print the JSON string representation of the object
print(GetBranchesParameters.to_json())

# convert the object into a dict
get_branches_parameters_dict = get_branches_parameters_instance.to_dict()
# create an instance of GetBranchesParameters from a dict
get_branches_parameters_from_dict = GetBranchesParameters.from_dict(get_branches_parameters_dict)
```
[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


