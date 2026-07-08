# ContentBlockDiscoveryCandidate

A bounded, response-protected candidate window for possible ContentBlock reuse.

## Properties

Name | Type | Description | Notes
------------ | ------------- | ------------- | -------------
**storage_pool_id** | **str** | StoragePool-wide CAS scope identifier. Public clients treat this as server-provided placement evidence and must not use it to select storage accounts, containers, buckets, prefixes, or write authorization directly. | [optional] 
**manifest_address** | **str** | Lowercase 64-character BLAKE3-derived FileManifest address. | [optional] 
**content_block_address** | **str** | Lowercase 64-character BLAKE3-derived ContentBlock address. | [optional] 
**ordinal_start** | **int** |  | [optional] 
**ordinal_count** | **int** |  | [optional] 
**metadata_version** | **int** |  | [optional] 
**matching_key_chunk_count** | **int** |  | [optional] 
**protected_chunk_addresses** | **List[str]** |  | [optional] 

## Example

```python
from grace_generated_openapi_probe.models.content_block_discovery_candidate import ContentBlockDiscoveryCandidate

# TODO update the JSON string below
json = "{}"
# create an instance of ContentBlockDiscoveryCandidate from a JSON string
content_block_discovery_candidate_instance = ContentBlockDiscoveryCandidate.from_json(json)
# print the JSON string representation of the object
print(ContentBlockDiscoveryCandidate.to_json())

# convert the object into a dict
content_block_discovery_candidate_dict = content_block_discovery_candidate_instance.to_dict()
# create an instance of ContentBlockDiscoveryCandidate from a dict
content_block_discovery_candidate_from_dict = ContentBlockDiscoveryCandidate.from_dict(content_block_discovery_candidate_dict)
```
[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


