# SignedCacheRequestProof


## Properties

Name | Type | Description | Notes
------------ | ------------- | ------------- | -------------
**var_class** | **str** |  | 
**payload** | [**CacheRequestProofPayload**](CacheRequestProofPayload.md) |  | 
**signature** | **str** |  | 

## Example

```python
from grace_generated_openapi_probe.models.signed_cache_request_proof import SignedCacheRequestProof

# TODO update the JSON string below
json = "{}"
# create an instance of SignedCacheRequestProof from a JSON string
signed_cache_request_proof_instance = SignedCacheRequestProof.from_json(json)
# print the JSON string representation of the object
print(SignedCacheRequestProof.to_json())

# convert the object into a dict
signed_cache_request_proof_dict = signed_cache_request_proof_instance.to_dict()
# create an instance of SignedCacheRequestProof from a dict
signed_cache_request_proof_from_dict = SignedCacheRequestProof.from_dict(signed_cache_request_proof_dict)
```
[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


