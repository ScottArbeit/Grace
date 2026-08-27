# SynchronizedReturnValueBase


## Properties

Name | Type | Description | Notes
------------ | ------------- | ------------- | -------------
**event_time** | **datetime** |  | 
**correlation_id** | **str** | Body DTO correlation id copied into Grace command/event metadata after request parsing. This field is distinct from the X-Correlation-Id transport header, which correlates the HTTP request/response exchange. | 
**properties** | **Dict[str, str]** |  | 

## Example

```python
from grace_generated_openapi_probe.models.synchronized_return_value_base import SynchronizedReturnValueBase

# TODO update the JSON string below
json = "{}"
# create an instance of SynchronizedReturnValueBase from a JSON string
synchronized_return_value_base_instance = SynchronizedReturnValueBase.from_json(json)
# print the JSON string representation of the object
print(SynchronizedReturnValueBase.to_json())

# convert the object into a dict
synchronized_return_value_base_dict = synchronized_return_value_base_instance.to_dict()
# create an instance of SynchronizedReturnValueBase from a dict
synchronized_return_value_base_from_dict = SynchronizedReturnValueBase.from_dict(synchronized_return_value_base_dict)
```
[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


