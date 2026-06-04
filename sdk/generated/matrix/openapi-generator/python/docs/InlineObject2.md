# InlineObject2


## Properties

Name | Type | Description | Notes
------------ | ------------- | ------------- | -------------
**return_value** | [**List[ApprovalRequest]**](ApprovalRequest.md) |  | [optional] 
**event_time** | **datetime** |  | [optional] 
**correlation_id** | **str** |  | [optional] 
**properties** | **Dict[str, str]** |  | [optional] 

## Example

```python
from grace_generated_openapi_probe.models.inline_object2 import InlineObject2

# TODO update the JSON string below
json = "{}"
# create an instance of InlineObject2 from a JSON string
inline_object2_instance = InlineObject2.from_json(json)
# print the JSON string representation of the object
print(InlineObject2.to_json())

# convert the object into a dict
inline_object2_dict = inline_object2_instance.to_dict()
# create an instance of InlineObject2 from a dict
inline_object2_from_dict = InlineObject2.from_dict(inline_object2_dict)
```
[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


