# ClaimedReuseRange

Reuse range accepted by the upload-session workflow.

## Properties

Name | Type | Description | Notes
------------ | ------------- | ------------- | -------------
**storage_pool_id** | **str** | StoragePool-wide CAS scope identifier. | 
**content_block_address** | **str** | Lowercase 64-character BLAKE3-derived ContentBlock address. | 
**ordinal_start** | **int** |  | 
**ordinal_count** | **int** |  | 
**physical_offset** | **int** |  | 
**physical_length** | **int** |  | 
**metadata_version** | **int** |  | 
**claimed_at** | **datetime** |  | 

## Example

```python
from grace_generated_openapi_probe.models.claimed_reuse_range import ClaimedReuseRange

# TODO update the JSON string below
json = "{}"
# create an instance of ClaimedReuseRange from a JSON string
claimed_reuse_range_instance = ClaimedReuseRange.from_json(json)
# print the JSON string representation of the object
print(ClaimedReuseRange.to_json())

# convert the object into a dict
claimed_reuse_range_dict = claimed_reuse_range_instance.to_dict()
# create an instance of ClaimedReuseRange from a dict
claimed_reuse_range_from_dict = ClaimedReuseRange.from_dict(claimed_reuse_range_dict)
```
[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


