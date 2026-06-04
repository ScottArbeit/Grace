# ContentBlockDiscoveryPolicy

Bounded, non-authoritative discovery policy returned by /storage/discoverContentBlocks.

## Properties

Name | Type | Description | Notes
------------ | ------------- | ------------- | -------------
**max_key_chunk_addresses** | **int** |  | [optional] 
**max_candidate_windows_per_key_chunk** | **int** |  | [optional] 
**max_window_chunks** | **int** |  | [optional] 
**max_response_protected_chunks** | **int** |  | [optional] 
**response_ttl_seconds** | **int** |  | [optional] 
**minimum_accepted_reuse_run_length** | **int** |  | [optional] 
**positive_candidates_enabled** | **bool** |  | [optional] 
**empty_response_means_absent** | **bool** |  | [optional] 
**is_authoritative** | **bool** |  | [optional] 

## Example

```python
from grace_generated_openapi_probe.models.content_block_discovery_policy import ContentBlockDiscoveryPolicy

# TODO update the JSON string below
json = "{}"
# create an instance of ContentBlockDiscoveryPolicy from a JSON string
content_block_discovery_policy_instance = ContentBlockDiscoveryPolicy.from_json(json)
# print the JSON string representation of the object
print(ContentBlockDiscoveryPolicy.to_json())

# convert the object into a dict
content_block_discovery_policy_dict = content_block_discovery_policy_instance.to_dict()
# create an instance of ContentBlockDiscoveryPolicy from a dict
content_block_discovery_policy_from_dict = ContentBlockDiscoveryPolicy.from_dict(content_block_discovery_policy_dict)
```
[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


