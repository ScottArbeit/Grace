# SynchronizedDeltaReturnValue


## Properties

Name | Type | Description | Notes
------------ | ------------- | ------------- | -------------
**event_time** | **datetime** |  | 
**correlation_id** | **str** | Body DTO correlation id copied into Grace command/event metadata after request parsing. This field is distinct from the X-Correlation-Id transport header, which correlates the HTTP request/response exchange. | 
**properties** | **Dict[str, str]** |  | 
**return_value** | [**SynchronizedDeltaResultDto**](SynchronizedDeltaResultDto.md) |  | [optional] 

## Example

```python
from grace_generated_openapi_probe.models.synchronized_delta_return_value import SynchronizedDeltaReturnValue

# TODO update the JSON string below
json = "{}"
# create an instance of SynchronizedDeltaReturnValue from a JSON string
synchronized_delta_return_value_instance = SynchronizedDeltaReturnValue.from_json(json)
# print the JSON string representation of the object
print(SynchronizedDeltaReturnValue.to_json())

# convert the object into a dict
synchronized_delta_return_value_dict = synchronized_delta_return_value_instance.to_dict()
# create an instance of SynchronizedDeltaReturnValue from a dict
synchronized_delta_return_value_from_dict = SynchronizedDeltaReturnValue.from_dict(synchronized_delta_return_value_dict)
```
[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


