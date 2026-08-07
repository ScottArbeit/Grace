# InlineObject9


## Properties

Name | Type | Description | Notes
------------ | ------------- | ------------- | -------------
**return_value** | **str** |  | [optional] 
**event_time** | **datetime** |  | [optional] 
**correlation_id** | **str** |  | [optional] 
**properties** | **Dict[str, str]** |  | [optional] 

## Example

```python
from grace_generated_openapi_probe.models.inline_object9 import InlineObject9

# TODO update the JSON string below
json = "{}"
# create an instance of InlineObject9 from a JSON string
inline_object9_instance = InlineObject9.from_json(json)
# print the JSON string representation of the object
print(InlineObject9.to_json())

# convert the object into a dict
inline_object9_dict = inline_object9_instance.to_dict()
# create an instance of InlineObject9 from a dict
inline_object9_from_dict = InlineObject9.from_dict(inline_object9_dict)
```
[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


