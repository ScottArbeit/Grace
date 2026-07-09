# CacheRegistrationReturnValue

Grace response envelope whose ReturnValue contains a Cache registration result.

## Properties

Name | Type | Description | Notes
------------ | ------------- | ------------- | -------------
**return_value** | [**CacheRegistrationResult**](CacheRegistrationResult.md) |  | 
**event_time** | **datetime** |  | 
**correlation_id** | **str** | Body DTO correlation id copied into Grace command/event metadata after request parsing. This field is distinct from the X-Correlation-Id transport header, which correlates the HTTP request/response exchange. | 
**properties** | **Dict[str, str]** |  | 

## Example

```python
from grace_generated_openapi_probe.models.cache_registration_return_value import CacheRegistrationReturnValue

# TODO update the JSON string below
json = "{}"
# create an instance of CacheRegistrationReturnValue from a JSON string
cache_registration_return_value_instance = CacheRegistrationReturnValue.from_json(json)
# print the JSON string representation of the object
print(CacheRegistrationReturnValue.to_json())

# convert the object into a dict
cache_registration_return_value_dict = cache_registration_return_value_instance.to_dict()
# create an instance of CacheRegistrationReturnValue from a dict
cache_registration_return_value_from_dict = CacheRegistrationReturnValue.from_dict(cache_registration_return_value_dict)
```
[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


