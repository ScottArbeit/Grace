# GraceReturnValueBase

Common metadata returned with successful Grace responses.

## Properties

Name | Type | Description | Notes
------------ | ------------- | ------------- | -------------
**event_time** | **datetime** |  | 
**correlation_id** | **str** |  | 
**properties** | **Dict[str, object]** |  | 

## Example

```python
from grace_generated_openapi_probe.models.grace_return_value_base import GraceReturnValueBase

# TODO update the JSON string below
json = "{}"
# create an instance of GraceReturnValueBase from a JSON string
grace_return_value_base_instance = GraceReturnValueBase.from_json(json)
# print the JSON string representation of the object
print(GraceReturnValueBase.to_json())

# convert the object into a dict
grace_return_value_base_dict = grace_return_value_base_instance.to_dict()
# create an instance of GraceReturnValueBase from a dict
grace_return_value_base_from_dict = GraceReturnValueBase.from_dict(grace_return_value_base_dict)
```
[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


