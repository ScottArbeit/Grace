# SignedArtifactRequestProof

Request-specific proof signed by the private half of the grant-bound ephemeral holder key.

## Properties

Name | Type | Description | Notes
------------ | ------------- | ------------- | -------------
**var_class** | **str** |  | 
**holder_public_key** | [**ArtifactGrantHolderPublicKey**](ArtifactGrantHolderPublicKey.md) |  | 
**payload** | [**ArtifactRequestProofPayload**](ArtifactRequestProofPayload.md) |  | 
**signature** | **str** | Base64url ECDSA signature over the canonical proof payload. | 

## Example

```python
from grace_generated_openapi_probe.models.signed_artifact_request_proof import SignedArtifactRequestProof

# TODO update the JSON string below
json = "{}"
# create an instance of SignedArtifactRequestProof from a JSON string
signed_artifact_request_proof_instance = SignedArtifactRequestProof.from_json(json)
# print the JSON string representation of the object
print(SignedArtifactRequestProof.to_json())

# convert the object into a dict
signed_artifact_request_proof_dict = signed_artifact_request_proof_instance.to_dict()
# create an instance of SignedArtifactRequestProof from a dict
signed_artifact_request_proof_from_dict = SignedArtifactRequestProof.from_dict(signed_artifact_request_proof_dict)
```
[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


