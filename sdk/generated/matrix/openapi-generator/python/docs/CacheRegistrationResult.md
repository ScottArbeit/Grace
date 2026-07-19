# CacheRegistrationResult

Result returned by Cache registration and refresh routes.

## Properties

Name | Type | Description | Notes
------------ | ------------- | ------------- | -------------
**var_class** | **str** |  | 
**status** | [**CacheRegistrationRefreshStatus**](CacheRegistrationRefreshStatus.md) |  | 
**registration** | [**CacheRegistration**](CacheRegistration.md) |  | [optional] 
**message** | **str** |  | 
**retry_after_seconds** | **int** |  | [optional] 

## Example

```python
from grace_generated_openapi_probe.models.cache_registration_result import CacheRegistrationResult

# TODO update the JSON string below
json = "{}"
# create an instance of CacheRegistrationResult from a JSON string
cache_registration_result_instance = CacheRegistrationResult.from_json(json)
# print the JSON string representation of the object
print(CacheRegistrationResult.to_json())

# convert the object into a dict
cache_registration_result_dict = cache_registration_result_instance.to_dict()
# create an instance of CacheRegistrationResult from a dict
cache_registration_result_from_dict = CacheRegistrationResult.from_dict(cache_registration_result_dict)
```
[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


