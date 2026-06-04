# OwnerParameters


## Properties

Name | Type | Description | Notes
------------ | ------------- | ------------- | -------------
**correlation_id** | **str** | Body DTO correlation id copied into Grace command/event metadata after request parsing. This field is distinct from the X-Correlation-Id transport header. | [optional] 
**principal** | **str** | The entity on whose behalf the action is being performed. | [optional] 
**owner_id** | **UUID** |  | [optional] 
**owner_name** | **str** |  | [optional] 

## Example

```python
from grace_generated_openapi_probe.models.owner_parameters import OwnerParameters

# TODO update the JSON string below
json = "{}"
# create an instance of OwnerParameters from a JSON string
owner_parameters_instance = OwnerParameters.from_json(json)
# print the JSON string representation of the object
print(OwnerParameters.to_json())

# convert the object into a dict
owner_parameters_dict = owner_parameters_instance.to_dict()
# create an instance of OwnerParameters from a dict
owner_parameters_from_dict = OwnerParameters.from_dict(owner_parameters_dict)
```
[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


