# CacheRevocationRequest


## Properties

Name | Type | Description | Notes
------------ | ------------- | ------------- | -------------
**var_class** | **str** |  | 
**cache_id** | **UUID** |  | 

## Example

```python
from grace_generated_openapi_probe.models.cache_revocation_request import CacheRevocationRequest

# TODO update the JSON string below
json = "{}"
# create an instance of CacheRevocationRequest from a JSON string
cache_revocation_request_instance = CacheRevocationRequest.from_json(json)
# print the JSON string representation of the object
print(CacheRevocationRequest.to_json())

# convert the object into a dict
cache_revocation_request_dict = cache_revocation_request_instance.to_dict()
# create an instance of CacheRevocationRequest from a dict
cache_revocation_request_from_dict = CacheRevocationRequest.from_dict(cache_revocation_request_dict)
```
[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


