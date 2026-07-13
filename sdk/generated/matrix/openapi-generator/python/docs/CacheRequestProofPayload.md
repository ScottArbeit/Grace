# CacheRequestProofPayload

Canonical Cache proof binding the cache id, operation, canonical request digest, and Unix-millisecond timestamp.

## Properties

Name | Type | Description | Notes
------------ | ------------- | ------------- | -------------
**var_class** | **str** |  | 
**cache_id** | **UUID** |  | 
**operation** | **str** |  | 
**request_digest** | **str** |  | 
**issued_at** | **datetime** |  | 

## Example

```python
from grace_generated_openapi_probe.models.cache_request_proof_payload import CacheRequestProofPayload

# TODO update the JSON string below
json = "{}"
# create an instance of CacheRequestProofPayload from a JSON string
cache_request_proof_payload_instance = CacheRequestProofPayload.from_json(json)
# print the JSON string representation of the object
print(CacheRequestProofPayload.to_json())

# convert the object into a dict
cache_request_proof_payload_dict = cache_request_proof_payload_instance.to_dict()
# create an instance of CacheRequestProofPayload from a dict
cache_request_proof_payload_from_dict = CacheRequestProofPayload.from_dict(cache_request_proof_payload_dict)
```
[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


