# ContentBlockStoragePlacement

Object-storage placement metadata for a confirmed ContentBlock payload.

## Properties

Name | Type | Description | Notes
------------ | ------------- | ------------- | -------------
**object_key** | **str** |  | 
**e_tag** | **str** |  | [optional] 

## Example

```python
from grace_generated_openapi_probe.models.content_block_storage_placement import ContentBlockStoragePlacement

# TODO update the JSON string below
json = "{}"
# create an instance of ContentBlockStoragePlacement from a JSON string
content_block_storage_placement_instance = ContentBlockStoragePlacement.from_json(json)
# print the JSON string representation of the object
print(ContentBlockStoragePlacement.to_json())

# convert the object into a dict
content_block_storage_placement_dict = content_block_storage_placement_instance.to_dict()
# create an instance of ContentBlockStoragePlacement from a dict
content_block_storage_placement_from_dict = ContentBlockStoragePlacement.from_dict(content_block_storage_placement_dict)
```
[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


