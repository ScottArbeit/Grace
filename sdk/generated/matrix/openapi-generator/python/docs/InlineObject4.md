# InlineObject4


## Properties

Name | Type | Description | Notes
------------ | ------------- | ------------- | -------------
**return_value** | [**WebhookRule**](WebhookRule.md) |  | [optional] 
**event_time** | **datetime** |  | [optional] 
**correlation_id** | **str** |  | [optional] 
**properties** | **Dict[str, str]** |  | [optional] 

## Example

```python
from grace_generated_openapi_probe.models.inline_object4 import InlineObject4

# TODO update the JSON string below
json = "{}"
# create an instance of InlineObject4 from a JSON string
inline_object4_instance = InlineObject4.from_json(json)
# print the JSON string representation of the object
print(InlineObject4.to_json())

# convert the object into a dict
inline_object4_dict = inline_object4_instance.to_dict()
# create an instance of InlineObject4 from a dict
inline_object4_from_dict = InlineObject4.from_dict(inline_object4_dict)
```
[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


