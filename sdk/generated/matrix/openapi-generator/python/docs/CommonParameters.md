# CommonParameters

Common parameters that are used across (almost) every endpoint in Grace.

## Properties

Name | Type | Description | Notes
------------ | ------------- | ------------- | -------------
**correlation_id** | **str** | Body DTO correlation id copied into Grace command/event metadata after request parsing. This field is distinct from the X-Correlation-Id transport header. | [optional] 
**principal** | **str** | The entity on whose behalf the action is being performed. | [optional] 

## Example

```python
from grace_generated_openapi_probe.models.common_parameters import CommonParameters

# TODO update the JSON string below
json = "{}"
# create an instance of CommonParameters from a JSON string
common_parameters_instance = CommonParameters.from_json(json)
# print the JSON string representation of the object
print(CommonParameters.to_json())

# convert the object into a dict
common_parameters_dict = common_parameters_instance.to_dict()
# create an instance of CommonParameters from a dict
common_parameters_from_dict = CommonParameters.from_dict(common_parameters_dict)
```
[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


