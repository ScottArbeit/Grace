# SynchronizedContentParameters

Repository locator shared by every remote synchronized-content request.

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

## Example

```python
from grace_generated_openapi_probe.models.synchronized_content_parameters import SynchronizedContentParameters

# TODO update the JSON string below
json = "{}"
# create an instance of SynchronizedContentParameters from a JSON string
synchronized_content_parameters_instance = SynchronizedContentParameters.from_json(json)
# print the JSON string representation of the object
print(SynchronizedContentParameters.to_json())

# convert the object into a dict
synchronized_content_parameters_dict = synchronized_content_parameters_instance.to_dict()
# create an instance of SynchronizedContentParameters from a dict
synchronized_content_parameters_from_dict = SynchronizedContentParameters.from_dict(synchronized_content_parameters_dict)
```
[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


