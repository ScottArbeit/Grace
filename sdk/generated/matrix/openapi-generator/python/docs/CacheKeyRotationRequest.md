# CacheKeyRotationRequest


## Properties

Name | Type | Description | Notes
------------ | ------------- | ------------- | -------------
**var_class** | **str** |  | 
**cache_id** | **UUID** |  | 
**new_public_key** | [**CacheIdentityPublicKey**](CacheIdentityPublicKey.md) |  | 
**proof** | [**SignedCacheRequestProof**](SignedCacheRequestProof.md) |  | 

## Example

```python
from grace_generated_openapi_probe.models.cache_key_rotation_request import CacheKeyRotationRequest

# TODO update the JSON string below
json = "{}"
# create an instance of CacheKeyRotationRequest from a JSON string
cache_key_rotation_request_instance = CacheKeyRotationRequest.from_json(json)
# print the JSON string representation of the object
print(CacheKeyRotationRequest.to_json())

# convert the object into a dict
cache_key_rotation_request_dict = cache_key_rotation_request_instance.to_dict()
# create an instance of CacheKeyRotationRequest from a dict
cache_key_rotation_request_from_dict = CacheKeyRotationRequest.from_dict(cache_key_rotation_request_dict)
```
[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


