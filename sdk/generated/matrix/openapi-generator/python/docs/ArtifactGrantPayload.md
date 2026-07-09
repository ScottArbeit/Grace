# ArtifactGrantPayload

Requester-, holder-, cache-, root-, mode-, and artifact-bound authorization payload.

## Properties

Name | Type | Description | Notes
------------ | ------------- | ------------- | -------------
**var_class** | **str** |  | 
**issuer** | **str** |  | 
**requester_principal_type** | [**ArtifactGrantRequesterPrincipalType**](ArtifactGrantRequesterPrincipalType.md) |  | 
**requester_principal_id** | **str** | Stable authenticated grace_user_id supplied by Grace Server. | 
**holder_key_thumbprint** | **str** | Base64url SHA-256 thumbprint of the canonical ephemeral holder public key. | 
**cache_service_principal_id** | **str** |  | 
**target_root_directory_version_id** | **UUID** |  | 
**execution_mode** | [**MaterializationExecutionMode**](MaterializationExecutionMode.md) |  | 
**artifact_identities** | **List[str]** |  | 
**issued_at** | **datetime** |  | 
**not_before** | **datetime** |  | 
**expires_at** | **datetime** |  | 

## Example

```python
from grace_generated_openapi_probe.models.artifact_grant_payload import ArtifactGrantPayload

# TODO update the JSON string below
json = "{}"
# create an instance of ArtifactGrantPayload from a JSON string
artifact_grant_payload_instance = ArtifactGrantPayload.from_json(json)
# print the JSON string representation of the object
print(ArtifactGrantPayload.to_json())

# convert the object into a dict
artifact_grant_payload_dict = artifact_grant_payload_instance.to_dict()
# create an instance of ArtifactGrantPayload from a dict
artifact_grant_payload_from_dict = ArtifactGrantPayload.from_dict(artifact_grant_payload_dict)
```
[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


