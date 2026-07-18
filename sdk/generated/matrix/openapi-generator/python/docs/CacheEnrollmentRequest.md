# CacheEnrollmentRequest

Administrator-authenticated enrollment for exactly one Owner or Organization and explicit repositories within it.

## Properties

Name | Type | Description | Notes
------------ | ------------- | ------------- | -------------
**var_class** | **str** |  | 
**display_name** | **str** |  | 
**boundary_kind** | [**CacheBoundaryKind**](CacheBoundaryKind.md) |  | 
**owner_id** | **UUID** |  | 
**organization_id** | **UUID** |  | [optional] 
**repository_scopes** | [**List[CacheRepositoryScope]**](CacheRepositoryScope.md) |  | 
**active_public_key** | [**CacheIdentityPublicKey**](CacheIdentityPublicKey.md) |  | 
**candidate_public_key** | [**CacheIdentityPublicKey**](CacheIdentityPublicKey.md) |  | [optional] 
**endpoint** | **str** |  | 
**allow_http_endpoint** | **bool** | Explicit administrator approval for this exact Endpoint to use HTTP instead of the HTTPS default. | 
**health** | [**CacheHealthStatus**](CacheHealthStatus.md) |  | 
**software_version** | **str** |  | 
**protocol_version** | **str** |  | 
**prefetch_supported** | **bool** |  | 

## Example

```python
from grace_generated_openapi_probe.models.cache_enrollment_request import CacheEnrollmentRequest

# TODO update the JSON string below
json = "{}"
# create an instance of CacheEnrollmentRequest from a JSON string
cache_enrollment_request_instance = CacheEnrollmentRequest.from_json(json)
# print the JSON string representation of the object
print(CacheEnrollmentRequest.to_json())

# convert the object into a dict
cache_enrollment_request_dict = cache_enrollment_request_instance.to_dict()
# create an instance of CacheEnrollmentRequest from a dict
cache_enrollment_request_from_dict = CacheEnrollmentRequest.from_dict(cache_enrollment_request_dict)
```
[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


