# GetOwnerParameters

Parameters for the /owner/get endpoint.

## Properties

Name | Type | Description | Notes
------------ | ------------- | ------------- | -------------
**correlation_id** | **str** | Body DTO correlation id copied into Grace command/event metadata after request parsing. This field is distinct from the X-Correlation-Id transport header. | [optional] 
**principal** | **str** | The entity on whose behalf the action is being performed. | [optional] 
**owner_id** | **UUID** |  | [optional] 
**owner_name** | **str** |  | [optional] 
**include_deleted** | **bool** |  | [optional] 

## Example

```python
from grace_generated_openapi_probe.models.get_owner_parameters import GetOwnerParameters

# TODO update the JSON string below
json = "{}"
# create an instance of GetOwnerParameters from a JSON string
get_owner_parameters_instance = GetOwnerParameters.from_json(json)
# print the JSON string representation of the object
print(GetOwnerParameters.to_json())

# convert the object into a dict
get_owner_parameters_dict = get_owner_parameters_instance.to_dict()
# create an instance of GetOwnerParameters from a dict
get_owner_parameters_from_dict = GetOwnerParameters.from_dict(get_owner_parameters_dict)
```
[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


