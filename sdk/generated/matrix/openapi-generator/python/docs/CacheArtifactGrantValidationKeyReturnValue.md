# CacheArtifactGrantValidationKeyReturnValue

Grace response envelope containing the current Server process validation key.

## Properties

Name | Type | Description | Notes
------------ | ------------- | ------------- | -------------
**return_value** | [**CacheArtifactGrantValidationKey**](CacheArtifactGrantValidationKey.md) |  | 
**event_time** | **datetime** |  | [optional] 
**correlation_id** | **str** |  | [optional] 
**properties** | **Dict[str, str]** |  | [optional] 

## Example

```python
from grace_generated_openapi_probe.models.cache_artifact_grant_validation_key_return_value import CacheArtifactGrantValidationKeyReturnValue

# TODO update the JSON string below
json = "{}"
# create an instance of CacheArtifactGrantValidationKeyReturnValue from a JSON string
cache_artifact_grant_validation_key_return_value_instance = CacheArtifactGrantValidationKeyReturnValue.from_json(json)
# print the JSON string representation of the object
print(CacheArtifactGrantValidationKeyReturnValue.to_json())

# convert the object into a dict
cache_artifact_grant_validation_key_return_value_dict = cache_artifact_grant_validation_key_return_value_instance.to_dict()
# create an instance of CacheArtifactGrantValidationKeyReturnValue from a dict
cache_artifact_grant_validation_key_return_value_from_dict = CacheArtifactGrantValidationKeyReturnValue.from_dict(cache_artifact_grant_validation_key_return_value_dict)
```
[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


