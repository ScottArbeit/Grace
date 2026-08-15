# InlineObject8


## Properties

Name | Type | Description | Notes
------------ | ------------- | ------------- | -------------
**return_value** | [**ArtifactDeletionResult**](ArtifactDeletionResult.md) |  | [optional] 
**event_time** | **datetime** |  | [optional] 
**correlation_id** | **str** |  | [optional] 
**properties** | **Dict[str, str]** |  | [optional] 

## Example

```python
from grace_generated_openapi_probe.models.inline_object8 import InlineObject8

# TODO update the JSON string below
json = "{}"
# create an instance of InlineObject8 from a JSON string
inline_object8_instance = InlineObject8.from_json(json)
# print the JSON string representation of the object
print(InlineObject8.to_json())

# convert the object into a dict
inline_object8_dict = inline_object8_instance.to_dict()
# create an instance of InlineObject8 from a dict
inline_object8_from_dict = InlineObject8.from_dict(inline_object8_dict)
```
[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


