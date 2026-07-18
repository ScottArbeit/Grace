# CacheKeyCandidateRequest

Active-key-proven submission of the one candidate Cache identity key. The candidate key promotes itself through a later refresh proof.

## Properties

Name | Type | Description | Notes
------------ | ------------- | ------------- | -------------
**var_class** | **str** |  | 
**cache_id** | **UUID** |  | 
**candidate_public_key** | [**CacheIdentityPublicKey**](CacheIdentityPublicKey.md) |  | 
**rotation_interval_minutes** | **int** |  | [default to 240]
**is_startup** | **bool** |  | 
**proof** | [**SignedCacheRequestProof**](SignedCacheRequestProof.md) |  | 

## Example

```python
from grace_generated_openapi_probe.models.cache_key_candidate_request import CacheKeyCandidateRequest

# TODO update the JSON string below
json = "{}"
# create an instance of CacheKeyCandidateRequest from a JSON string
cache_key_candidate_request_instance = CacheKeyCandidateRequest.from_json(json)
# print the JSON string representation of the object
print(CacheKeyCandidateRequest.to_json())

# convert the object into a dict
cache_key_candidate_request_dict = cache_key_candidate_request_instance.to_dict()
# create an instance of CacheKeyCandidateRequest from a dict
cache_key_candidate_request_from_dict = CacheKeyCandidateRequest.from_dict(cache_key_candidate_request_dict)
```
[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


