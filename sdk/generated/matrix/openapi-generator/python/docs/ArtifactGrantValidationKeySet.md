# ArtifactGrantValidationKeySet

Validation-key publication for local artifact grant verification by Grace Cache.

## Properties

Name | Type | Description | Notes
------------ | ------------- | ------------- | -------------
**var_class** | **str** |  | 
**issuer** | **str** |  | 
**published_at** | **datetime** |  | 
**cache_ttl** | **str** | Validation-key cache lifetime serialized as a NodaTime duration. | 
**keys** | [**List[ArtifactGrantValidationKey]**](ArtifactGrantValidationKey.md) |  | 

## Example

```python
from grace_generated_openapi_probe.models.artifact_grant_validation_key_set import ArtifactGrantValidationKeySet

# TODO update the JSON string below
json = "{}"
# create an instance of ArtifactGrantValidationKeySet from a JSON string
artifact_grant_validation_key_set_instance = ArtifactGrantValidationKeySet.from_json(json)
# print the JSON string representation of the object
print(ArtifactGrantValidationKeySet.to_json())

# convert the object into a dict
artifact_grant_validation_key_set_dict = artifact_grant_validation_key_set_instance.to_dict()
# create an instance of ArtifactGrantValidationKeySet from a dict
artifact_grant_validation_key_set_from_dict = ArtifactGrantValidationKeySet.from_dict(artifact_grant_validation_key_set_dict)
```
[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


