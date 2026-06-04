# InlineObject5


## Properties

Name | Type | Description | Notes
------------ | ------------- | ------------- | -------------
**return_value** | [**List[WebhookRule]**](WebhookRule.md) |  | [optional] 
**event_time** | **datetime** |  | [optional] 
**correlation_id** | **str** |  | [optional] 
**properties** | **Dict[str, str]** |  | [optional] 

## Example

```python
from grace_generated_openapi_probe.models.inline_object5 import InlineObject5

# TODO update the JSON string below
json = "{}"
# create an instance of InlineObject5 from a JSON string
inline_object5_instance = InlineObject5.from_json(json)
# print the JSON string representation of the object
print(InlineObject5.to_json())

# convert the object into a dict
inline_object5_dict = inline_object5_instance.to_dict()
# create an instance of InlineObject5 from a dict
inline_object5_from_dict = InlineObject5.from_dict(inline_object5_dict)
```
[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


