# CacheRegistration

Durable Cache registration with immutable CacheId, explicit repository assignments, and no private key material.

## Properties

Name | Type | Description | Notes
------------ | ------------- | ------------- | -------------
**var_class** | **str** |  | 
**cache_id** | **UUID** |  | 
**display_name** | **str** |  | 
**boundary_kind** | [**CacheBoundaryKind**](CacheBoundaryKind.md) |  | 
**owner_id** | **UUID** |  | 
**organization_id** | **UUID** |  | [optional] 
**repository_scopes** | [**List[CacheRepositoryScope]**](CacheRepositoryScope.md) |  | 
**public_key** | [**CacheIdentityPublicKey**](CacheIdentityPublicKey.md) |  | 
**endpoint** | **str** |  | 
**health** | [**CacheHealthStatus**](CacheHealthStatus.md) |  | 
**software_version** | **str** |  | 
**protocol_version** | **str** |  | 
**prefetch_supported** | **bool** |  | 
**enrolled_by** | **str** |  | 
**enrolled_at** | **datetime** |  | 
**last_refreshed_at** | **datetime** |  | 
**refresh_after** | **datetime** |  | 
**expires_at** | **datetime** |  | 
**rotation_due_at** | **datetime** |  | 
**revoked_at** | **datetime** |  | [optional] 

## Example

```python
from grace_generated_openapi_probe.models.cache_registration import CacheRegistration

# TODO update the JSON string below
json = "{}"
# create an instance of CacheRegistration from a JSON string
cache_registration_instance = CacheRegistration.from_json(json)
# print the JSON string representation of the object
print(CacheRegistration.to_json())

# convert the object into a dict
cache_registration_dict = cache_registration_instance.to_dict()
# create an instance of CacheRegistration from a dict
cache_registration_from_dict = CacheRegistration.from_dict(cache_registration_dict)
```
[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


