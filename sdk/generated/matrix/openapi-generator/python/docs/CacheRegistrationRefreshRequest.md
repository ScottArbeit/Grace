# CacheRegistrationRefreshRequest

Cache-authenticated refresh that may update operational facts only.

## Properties

Name | Type | Description | Notes
------------ | ------------- | ------------- | -------------
**var_class** | **str** |  | 
**cache_id** | **UUID** |  | 
**endpoint** | **str** |  | 
**health** | [**CacheHealthStatus**](CacheHealthStatus.md) |  | 
**software_version** | **str** |  | 
**protocol_version** | **str** |  | 
**prefetch_supported** | **bool** |  | 
**observed_at** | **datetime** |  | 
**proof** | [**SignedCacheRequestProof**](SignedCacheRequestProof.md) |  | 

## Example

```python
from grace_generated_openapi_probe.models.cache_registration_refresh_request import CacheRegistrationRefreshRequest

# TODO update the JSON string below
json = "{}"
# create an instance of CacheRegistrationRefreshRequest from a JSON string
cache_registration_refresh_request_instance = CacheRegistrationRefreshRequest.from_json(json)
# print the JSON string representation of the object
print(CacheRegistrationRefreshRequest.to_json())

# convert the object into a dict
cache_registration_refresh_request_dict = cache_registration_refresh_request_instance.to_dict()
# create an instance of CacheRegistrationRefreshRequest from a dict
cache_registration_refresh_request_from_dict = CacheRegistrationRefreshRequest.from_dict(cache_registration_refresh_request_dict)
```
[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


