# DiscoverContentBlocksReturnValue

Successful ContentBlock discovery response.

## Properties

Name | Type | Description | Notes
------------ | ------------- | ------------- | -------------
**event_time** | **datetime** |  | 
**correlation_id** | **str** |  | 
**properties** | **Dict[str, object]** |  | 
**return_value** | [**DiscoverContentBlocksResult**](DiscoverContentBlocksResult.md) |  | 

## Example

```python
from grace_generated_openapi_probe.models.discover_content_blocks_return_value import DiscoverContentBlocksReturnValue

# TODO update the JSON string below
json = "{}"
# create an instance of DiscoverContentBlocksReturnValue from a JSON string
discover_content_blocks_return_value_instance = DiscoverContentBlocksReturnValue.from_json(json)
# print the JSON string representation of the object
print(DiscoverContentBlocksReturnValue.to_json())

# convert the object into a dict
discover_content_blocks_return_value_dict = discover_content_blocks_return_value_instance.to_dict()
# create an instance of DiscoverContentBlocksReturnValue from a dict
discover_content_blocks_return_value_from_dict = DiscoverContentBlocksReturnValue.from_dict(discover_content_blocks_return_value_dict)
```
[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


