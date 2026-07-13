# CacheIdentityPublicKey

Canonical P-256 public key for a Cache identity. Private key material is never accepted or returned.

## Properties

Name | Type | Description | Notes
------------ | ------------- | ------------- | -------------
**var_class** | **str** |  | 
**algorithm** | **str** |  | 
**curve** | **str** |  | 
**public_key_x** | **str** |  | 
**public_key_y** | **str** |  | 

## Example

```python
from grace_generated_openapi_probe.models.cache_identity_public_key import CacheIdentityPublicKey

# TODO update the JSON string below
json = "{}"
# create an instance of CacheIdentityPublicKey from a JSON string
cache_identity_public_key_instance = CacheIdentityPublicKey.from_json(json)
# print the JSON string representation of the object
print(CacheIdentityPublicKey.to_json())

# convert the object into a dict
cache_identity_public_key_dict = cache_identity_public_key_instance.to_dict()
# create an instance of CacheIdentityPublicKey from a dict
cache_identity_public_key_from_dict = CacheIdentityPublicKey.from_dict(cache_identity_public_key_dict)
```
[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


