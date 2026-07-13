# CacheRepositoryScope

Explicit durable repository assignment with its organization needed for authoritative lookup.

## Properties

Name | Type | Description | Notes
------------ | ------------- | ------------- | -------------
**var_class** | **str** |  | 
**organization_id** | **UUID** |  | 
**repository_id** | **UUID** |  | 

## Example

```python
from grace_generated_openapi_probe.models.cache_repository_scope import CacheRepositoryScope

# TODO update the JSON string below
json = "{}"
# create an instance of CacheRepositoryScope from a JSON string
cache_repository_scope_instance = CacheRepositoryScope.from_json(json)
# print the JSON string representation of the object
print(CacheRepositoryScope.to_json())

# convert the object into a dict
cache_repository_scope_dict = cache_repository_scope_instance.to_dict()
# create an instance of CacheRepositoryScope from a dict
cache_repository_scope_from_dict = CacheRepositoryScope.from_dict(cache_repository_scope_dict)
```
[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


