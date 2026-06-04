# ContentBlockMetadataRange

Physical range metadata for ContentBlock reconstruction, reuse, and compaction.

## Properties

Name | Type | Description | Notes
------------ | ------------- | ------------- | -------------
**ordinal_start** | **int** |  | 
**ordinal_count** | **int** |  | 
**active_manifest_count** | **int** |  | 
**physical_offset** | **int** |  | 
**physical_length** | **int** |  | 

## Example

```python
from grace_generated_openapi_probe.models.content_block_metadata_range import ContentBlockMetadataRange

# TODO update the JSON string below
json = "{}"
# create an instance of ContentBlockMetadataRange from a JSON string
content_block_metadata_range_instance = ContentBlockMetadataRange.from_json(json)
# print the JSON string representation of the object
print(ContentBlockMetadataRange.to_json())

# convert the object into a dict
content_block_metadata_range_dict = content_block_metadata_range_instance.to_dict()
# create an instance of ContentBlockMetadataRange from a dict
content_block_metadata_range_from_dict = ContentBlockMetadataRange.from_dict(content_block_metadata_range_dict)
```
[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


