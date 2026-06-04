# SetOwnerTypeParameters

Parameters for the /owner/setType endpoint.

## Properties

Name | Type | Description | Notes
------------ | ------------- | ------------- | -------------
**correlation_id** | **str** | Body DTO correlation id copied into Grace command/event metadata after request parsing. This field is distinct from the X-Correlation-Id transport header. | [optional] 
**principal** | **str** | The entity on whose behalf the action is being performed. | [optional] 
**owner_id** | **UUID** |  | [optional] 
**owner_name** | **str** |  | [optional] 
**owner_type** | [**OwnerType**](OwnerType.md) |  | [optional] 

## Example

```python
from grace_generated_openapi_probe.models.set_owner_type_parameters import SetOwnerTypeParameters

# TODO update the JSON string below
json = "{}"
# create an instance of SetOwnerTypeParameters from a JSON string
set_owner_type_parameters_instance = SetOwnerTypeParameters.from_json(json)
# print the JSON string representation of the object
print(SetOwnerTypeParameters.to_json())

# convert the object into a dict
set_owner_type_parameters_dict = set_owner_type_parameters_instance.to_dict()
# create an instance of SetOwnerTypeParameters from a dict
set_owner_type_parameters_from_dict = SetOwnerTypeParameters.from_dict(set_owner_type_parameters_dict)
```
[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


