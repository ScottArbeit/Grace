# ArtifactRequestProofPayload

Canonical statement binding one short-lived holder proof to an exact artifact request.

## Properties

Name | Type | Description | Notes
------------ | ------------- | ------------- | -------------
**var_class** | **str** |  | 
**grant_digest** | **str** | Base64url SHA-256 digest of the complete signed grant envelope. | 
**http_method** | **str** |  | 
**normalized_route** | **str** |  | 
**artifact_identity** | **str** |  | 
**issued_at** | **datetime** |  | 
**expires_at** | **datetime** |  | 

## Example

```python
from grace_generated_openapi_probe.models.artifact_request_proof_payload import ArtifactRequestProofPayload

# TODO update the JSON string below
json = "{}"
# create an instance of ArtifactRequestProofPayload from a JSON string
artifact_request_proof_payload_instance = ArtifactRequestProofPayload.from_json(json)
# print the JSON string representation of the object
print(ArtifactRequestProofPayload.to_json())

# convert the object into a dict
artifact_request_proof_payload_dict = artifact_request_proof_payload_instance.to_dict()
# create an instance of ArtifactRequestProofPayload from a dict
artifact_request_proof_payload_from_dict = ArtifactRequestProofPayload.from_dict(artifact_request_proof_payload_dict)
```
[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


