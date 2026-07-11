# GetBranchesByBranchIdParameters

Parameters for the /repository/getBranchesByBranchId endpoint.

## Properties

Name | Type | Description | Notes
------------ | ------------- | ------------- | -------------
**correlation_id** | **str** | Body DTO correlation id copied into Grace command/event metadata after request parsing. This field is distinct from the X-Correlation-Id transport header. | [optional] 
**principal** | **str** | The entity on whose behalf the action is being performed. | [optional] 
**properties** | **Dict[str, str]** | Allow-listed event properties. UploadSessionIds is the only client-settable key. | [optional] 
**owner_id** | **UUID** |  | [optional] 
**owner_name** | **str** |  | [optional] 
**organization_id** | **UUID** |  | [optional] 
**organization_name** | **str** |  | [optional] 
**repository_id** | **UUID** |  | [optional] 
**repository_name** | **str** |  | [optional] 
**branch_ids** | **List[UUID]** |  | [optional] 
**max_count** | **int** |  | [optional] 
**include_deleted** | **bool** |  | [optional] 

## Example

```python
from grace_generated_openapi_probe.models.get_branches_by_branch_id_parameters import GetBranchesByBranchIdParameters

# TODO update the JSON string below
json = "{}"
# create an instance of GetBranchesByBranchIdParameters from a JSON string
get_branches_by_branch_id_parameters_instance = GetBranchesByBranchIdParameters.from_json(json)
# print the JSON string representation of the object
print(GetBranchesByBranchIdParameters.to_json())

# convert the object into a dict
get_branches_by_branch_id_parameters_dict = get_branches_by_branch_id_parameters_instance.to_dict()
# create an instance of GetBranchesByBranchIdParameters from a dict
get_branches_by_branch_id_parameters_from_dict = GetBranchesByBranchIdParameters.from_dict(get_branches_by_branch_id_parameters_dict)
```
[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


