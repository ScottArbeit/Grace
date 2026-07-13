# SignedArtifactGrant

ES256 artifact grant envelope returned only for non-Direct materialization.

## Properties

Name | Type | Description | Notes
------------ | ------------- | ------------- | -------------
**var_class** | **str** |  | 
**header** | [**ArtifactGrantHeader**](ArtifactGrantHeader.md) |  | 
**payload** | [**ArtifactGrantPayload**](ArtifactGrantPayload.md) |  | 
**signature** | **str** | Base64url ECDSA signature over the canonical header and payload. | 

## Example

```python
from grace_generated_openapi_probe.models.signed_artifact_grant import SignedArtifactGrant

# TODO update the JSON string below
json = "{}"
# create an instance of SignedArtifactGrant from a JSON string
signed_artifact_grant_instance = SignedArtifactGrant.from_json(json)
# print the JSON string representation of the object
print(SignedArtifactGrant.to_json())

# convert the object into a dict
signed_artifact_grant_dict = signed_artifact_grant_instance.to_dict()
# create an instance of SignedArtifactGrant from a dict
signed_artifact_grant_from_dict = SignedArtifactGrant.from_dict(signed_artifact_grant_dict)
```
[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


