# ArtifactGrantValidationKey

Public P-256 validation key used by Grace Cache to verify signed artifact grants.

## Properties

Name | Type | Description | Notes
------------ | ------------- | ------------- | -------------
**var_class** | **str** |  | 
**key_id** | **str** |  | 
**algorithm** | **str** |  | 
**created_at** | **datetime** |  | 
**not_before** | **datetime** |  | 
**expires_at** | **datetime** |  | 
**public_key_x** | **str** | Base64url-encoded P-256 X coordinate. | 
**public_key_y** | **str** | Base64url-encoded P-256 Y coordinate. | 

## Example

```python
from grace_generated_openapi_probe.models.artifact_grant_validation_key import ArtifactGrantValidationKey

# TODO update the JSON string below
json = "{}"
# create an instance of ArtifactGrantValidationKey from a JSON string
artifact_grant_validation_key_instance = ArtifactGrantValidationKey.from_json(json)
# print the JSON string representation of the object
print(ArtifactGrantValidationKey.to_json())

# convert the object into a dict
artifact_grant_validation_key_dict = artifact_grant_validation_key_instance.to_dict()
# create an instance of ArtifactGrantValidationKey from a dict
artifact_grant_validation_key_from_dict = ArtifactGrantValidationKey.from_dict(artifact_grant_validation_key_dict)
```
[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


