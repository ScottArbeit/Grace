# ArtifactGrantHolderPublicKey

Canonical public half of the client's ephemeral P-256 holder key.

## Properties

Name | Type | Description | Notes
------------ | ------------- | ------------- | -------------
**var_class** | **str** |  | 
**algorithm** | **str** |  | 
**curve** | **str** |  | 
**public_key_x** | **str** | Base64url-encoded 32-byte P-256 X coordinate. | 
**public_key_y** | **str** | Base64url-encoded 32-byte P-256 Y coordinate. | 

## Example

```python
from grace_generated_openapi_probe.models.artifact_grant_holder_public_key import ArtifactGrantHolderPublicKey

# TODO update the JSON string below
json = "{}"
# create an instance of ArtifactGrantHolderPublicKey from a JSON string
artifact_grant_holder_public_key_instance = ArtifactGrantHolderPublicKey.from_json(json)
# print the JSON string representation of the object
print(ArtifactGrantHolderPublicKey.to_json())

# convert the object into a dict
artifact_grant_holder_public_key_dict = artifact_grant_holder_public_key_instance.to_dict()
# create an instance of ArtifactGrantHolderPublicKey from a dict
artifact_grant_holder_public_key_from_dict = ArtifactGrantHolderPublicKey.from_dict(artifact_grant_holder_public_key_dict)
```
[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


