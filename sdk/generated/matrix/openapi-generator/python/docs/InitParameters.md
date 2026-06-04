# InitParameters

Parameters for the /repository/init endpoint.

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
**grace_config** | **str** |  | [optional] 

## Example

```python
from grace_generated_openapi_probe.models.init_parameters import InitParameters

# TODO update the JSON string below
json = "{}"
# create an instance of InitParameters from a JSON string
init_parameters_instance = InitParameters.from_json(json)
# print the JSON string representation of the object
print(InitParameters.to_json())

# convert the object into a dict
init_parameters_dict = init_parameters_instance.to_dict()
# create an instance of InitParameters from a dict
init_parameters_from_dict = InitParameters.from_dict(init_parameters_dict)
```
[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


