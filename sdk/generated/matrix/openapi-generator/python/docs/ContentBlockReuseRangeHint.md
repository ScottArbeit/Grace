# ContentBlockReuseRangeHint

Server-issued hint used to claim a reusable ContentBlock range.

## Properties

Name | Type | Description | Notes
------------ | ------------- | ------------- | -------------
**storage_pool_id** | **str** | StoragePool-wide CAS scope identifier. Public clients treat this as server-provided placement evidence and must not use it to select storage accounts, containers, buckets, prefixes, or write authority directly. | [optional] 
**manifest_address** | **str** | Lowercase 64-character BLAKE3-derived FileManifest address. | [optional] 
**content_block_address** | **str** | Lowercase 64-character BLAKE3-derived ContentBlock address. | [optional] 
**ordinal_start** | **int** |  | [optional] 
**ordinal_count** | **int** |  | [optional] 
**metadata_version** | **int** |  | [optional] 
**protected_chunk_addresses** | **List[str]** |  | [optional] 

## Example

```python
from grace_generated_openapi_probe.models.content_block_reuse_range_hint import ContentBlockReuseRangeHint

# TODO update the JSON string below
json = "{}"
# create an instance of ContentBlockReuseRangeHint from a JSON string
content_block_reuse_range_hint_instance = ContentBlockReuseRangeHint.from_json(json)
# print the JSON string representation of the object
print(ContentBlockReuseRangeHint.to_json())

# convert the object into a dict
content_block_reuse_range_hint_dict = content_block_reuse_range_hint_instance.to_dict()
# create an instance of ContentBlockReuseRangeHint from a dict
content_block_reuse_range_hint_from_dict = ContentBlockReuseRangeHint.from_dict(content_block_reuse_range_hint_dict)
```
[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


