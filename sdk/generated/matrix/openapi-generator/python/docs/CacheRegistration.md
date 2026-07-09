# CacheRegistration

Server-owned active registration record for an approved Grace Cache service.

## Properties

Name | Type | Description | Notes
------------ | ------------- | ------------- | -------------
**var_class** | **str** |  | 
**service_principal_id** | **str** |  | 
**endpoint** | **str** |  | 
**approved_scopes** | **List[str]** |  | 
**approved_capabilities** | **List[str]** |  | 
**approved_execution_modes** | [**List[MaterializationExecutionMode]**](MaterializationExecutionMode.md) |  | 
**registered_at** | **datetime** |  | 
**last_refreshed_at** | **datetime** |  | 
**refresh_after** | **datetime** |  | 
**expires_at** | **datetime** |  | 
**read_through_enabled** | **bool** |  | 
**prefetch_enabled** | **bool** |  | 

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


