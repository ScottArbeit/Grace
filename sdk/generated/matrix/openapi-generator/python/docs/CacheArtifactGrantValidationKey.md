# CacheArtifactGrantValidationKey


## Properties

Name | Type | Description | Notes
------------ | ------------- | ------------- | -------------
**issuer** | **str** |  | 
**audience** | **str** |  | 
**algorithm** | **str** |  | 
**key_id** | **str** |  | 
**public_jwk** | [**P256PublicJwk**](P256PublicJwk.md) |  | 

## Example

```python
from grace_generated_openapi_probe.models.cache_artifact_grant_validation_key import CacheArtifactGrantValidationKey

# TODO update the JSON string below
json = "{}"
# create an instance of CacheArtifactGrantValidationKey from a JSON string
cache_artifact_grant_validation_key_instance = CacheArtifactGrantValidationKey.from_json(json)
# print the JSON string representation of the object
print(CacheArtifactGrantValidationKey.to_json())

# convert the object into a dict
cache_artifact_grant_validation_key_dict = cache_artifact_grant_validation_key_instance.to_dict()
# create an instance of CacheArtifactGrantValidationKey from a dict
cache_artifact_grant_validation_key_from_dict = CacheArtifactGrantValidationKey.from_dict(cache_artifact_grant_validation_key_dict)
```
[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


