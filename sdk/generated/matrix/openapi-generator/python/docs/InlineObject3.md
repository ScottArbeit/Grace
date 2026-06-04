# InlineObject3


## Properties

Name | Type | Description | Notes
------------ | ------------- | ------------- | -------------
**return_value** | [**ApprovalRequest**](ApprovalRequest.md) |  | [optional] 
**event_time** | **datetime** |  | [optional] 
**correlation_id** | **str** |  | [optional] 
**properties** | **Dict[str, str]** |  | [optional] 

## Example

```python
from grace_generated_openapi_probe.models.inline_object3 import InlineObject3

# TODO update the JSON string below
json = "{}"
# create an instance of InlineObject3 from a JSON string
inline_object3_instance = InlineObject3.from_json(json)
# print the JSON string representation of the object
print(InlineObject3.to_json())

# convert the object into a dict
inline_object3_dict = inline_object3_instance.to_dict()
# create an instance of InlineObject3 from a dict
inline_object3_from_dict = InlineObject3.from_dict(inline_object3_dict)
```
[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


