# SetOwnerSearchVisibilityParameters

Parameters for the /owner/setSearchVisibility endpoint.

## Properties

Name | Type | Description | Notes
------------ | ------------- | ------------- | -------------
**correlation_id** | **str** | Body DTO correlation id copied into Grace command/event metadata after request parsing. This field is distinct from the X-Correlation-Id transport header. | [optional] 
**principal** | **str** | The entity on whose behalf the action is being performed. | [optional] 
**owner_id** | **UUID** |  | [optional] 
**owner_name** | **str** |  | [optional] 
**search_visibility** | [**SearchVisibility**](SearchVisibility.md) |  | [optional] 

## Example

```python
from grace_generated_openapi_probe.models.set_owner_search_visibility_parameters import SetOwnerSearchVisibilityParameters

# TODO update the JSON string below
json = "{}"
# create an instance of SetOwnerSearchVisibilityParameters from a JSON string
set_owner_search_visibility_parameters_instance = SetOwnerSearchVisibilityParameters.from_json(json)
# print the JSON string representation of the object
print(SetOwnerSearchVisibilityParameters.to_json())

# convert the object into a dict
set_owner_search_visibility_parameters_dict = set_owner_search_visibility_parameters_instance.to_dict()
# create an instance of SetOwnerSearchVisibilityParameters from a dict
set_owner_search_visibility_parameters_from_dict = SetOwnerSearchVisibilityParameters.from_dict(set_owner_search_visibility_parameters_dict)
```
[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


