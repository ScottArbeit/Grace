# ReferenceReplayReturnValue

Grace response envelope containing one closed branch Reference replay interval.

## Properties

Name | Type | Description | Notes
------------ | ------------- | ------------- | -------------
**return_value** | [**ReferenceReplayApiDto**](ReferenceReplayApiDto.md) |  | 
**event_time** | **datetime** |  | 
**correlation_id** | **str** | Body DTO correlation id copied into Grace command/event metadata after request parsing. This field is distinct from the X-Correlation-Id transport header, which correlates the HTTP request/response exchange. | 
**properties** | **Dict[str, str]** |  | 

## Example

```python
from grace_generated_openapi_probe.models.reference_replay_return_value import ReferenceReplayReturnValue

# TODO update the JSON string below
json = "{}"
# create an instance of ReferenceReplayReturnValue from a JSON string
reference_replay_return_value_instance = ReferenceReplayReturnValue.from_json(json)
# print the JSON string representation of the object
print(ReferenceReplayReturnValue.to_json())

# convert the object into a dict
reference_replay_return_value_dict = reference_replay_return_value_instance.to_dict()
# create an instance of ReferenceReplayReturnValue from a dict
reference_replay_return_value_from_dict = ReferenceReplayReturnValue.from_dict(reference_replay_return_value_dict)
```
[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


