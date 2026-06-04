# SetOwnerNameParameters

Parameters for the /owner/setName endpoint.

## Properties

Name | Type | Description | Notes
------------ | ------------- | ------------- | -------------
**correlation_id** | **str** | Body DTO correlation id copied into Grace command/event metadata after request parsing. This field is distinct from the X-Correlation-Id transport header. | [optional] 
**principal** | **str** | The entity on whose behalf the action is being performed. | [optional] 
**owner_id** | **UUID** |  | [optional] 
**owner_name** | **str** |  | [optional] 
**new_name** | **str** |  | [optional] 

## Example

```python
from grace_generated_openapi_probe.models.set_owner_name_parameters import SetOwnerNameParameters

# TODO update the JSON string below
json = "{}"
# create an instance of SetOwnerNameParameters from a JSON string
set_owner_name_parameters_instance = SetOwnerNameParameters.from_json(json)
# print the JSON string representation of the object
print(SetOwnerNameParameters.to_json())

# convert the object into a dict
set_owner_name_parameters_dict = set_owner_name_parameters_instance.to_dict()
# create an instance of SetOwnerNameParameters from a dict
set_owner_name_parameters_from_dict = SetOwnerNameParameters.from_dict(set_owner_name_parameters_dict)
```
[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


