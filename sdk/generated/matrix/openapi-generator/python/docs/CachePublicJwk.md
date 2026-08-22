# CachePublicJwk


## Properties

Name | Type | Description | Notes
------------ | ------------- | ------------- | -------------
**kty** | **str** |  | 
**crv** | **str** |  | 
**x** | **str** |  | 
**y** | **str** |  | 

## Example

```python
from grace_generated_openapi_probe.models.cache_public_jwk import CachePublicJwk

# TODO update the JSON string below
json = "{}"
# create an instance of CachePublicJwk from a JSON string
cache_public_jwk_instance = CachePublicJwk.from_json(json)
# print the JSON string representation of the object
print(CachePublicJwk.to_json())

# convert the object into a dict
cache_public_jwk_dict = cache_public_jwk_instance.to_dict()
# create an instance of CachePublicJwk from a dict
cache_public_jwk_from_dict = CachePublicJwk.from_dict(cache_public_jwk_dict)
```
[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


