# SetRepositoryVisibilityParameters

Parameters for the /repository/setVisibility endpoint.

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
**visibility** | [**RepositoryVisibility**](RepositoryVisibility.md) |  | [optional] 

## Example

```python
from grace_generated_openapi_probe.models.set_repository_visibility_parameters import SetRepositoryVisibilityParameters

# TODO update the JSON string below
json = "{}"
# create an instance of SetRepositoryVisibilityParameters from a JSON string
set_repository_visibility_parameters_instance = SetRepositoryVisibilityParameters.from_json(json)
# print the JSON string representation of the object
print(SetRepositoryVisibilityParameters.to_json())

# convert the object into a dict
set_repository_visibility_parameters_dict = set_repository_visibility_parameters_instance.to_dict()
# create an instance of SetRepositoryVisibilityParameters from a dict
set_repository_visibility_parameters_from_dict = SetRepositoryVisibilityParameters.from_dict(set_repository_visibility_parameters_dict)
```
[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


