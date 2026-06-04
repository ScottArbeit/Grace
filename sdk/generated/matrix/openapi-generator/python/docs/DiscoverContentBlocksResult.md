# DiscoverContentBlocksResult

Non-authoritative ContentBlock discovery result.

## Properties

Name | Type | Description | Notes
------------ | ------------- | ------------- | -------------
**requested_key_chunk_count** | **int** |  | [optional] 
**accepted_key_chunk_count** | **int** |  | [optional] 
**policy** | [**ContentBlockDiscoveryPolicy**](ContentBlockDiscoveryPolicy.md) |  | [optional] 
**candidate_content_blocks** | [**List[ContentBlockDiscoveryCandidate]**](ContentBlockDiscoveryCandidate.md) |  | [optional] 
**is_partial** | **bool** |  | [optional] 
**message** | **str** |  | [optional] 

## Example

```python
from grace_generated_openapi_probe.models.discover_content_blocks_result import DiscoverContentBlocksResult

# TODO update the JSON string below
json = "{}"
# create an instance of DiscoverContentBlocksResult from a JSON string
discover_content_blocks_result_instance = DiscoverContentBlocksResult.from_json(json)
# print the JSON string representation of the object
print(DiscoverContentBlocksResult.to_json())

# convert the object into a dict
discover_content_blocks_result_dict = discover_content_blocks_result_instance.to_dict()
# create an instance of DiscoverContentBlocksResult from a dict
discover_content_blocks_result_from_dict = DiscoverContentBlocksResult.from_dict(discover_content_blocks_result_dict)
```
[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


