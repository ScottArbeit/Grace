# StartManifestUploadSessionParameters


## Properties

Name | Type | Description | Notes
------------ | ------------- | ------------- | -------------
**correlation_id** | **str** | Body DTO correlation id copied into Grace command/event metadata after request parsing. This field is distinct from the X-Correlation-Id transport header. | [optional] 
**principal** | **str** | The entity on whose behalf the action is being performed. | [optional] 
**owner_id** | **str** |  | [optional] 
**owner_name** | **str** |  | [optional] 
**organization_id** | **str** |  | [optional] 
**organization_name** | **str** |  | [optional] 
**repository_id** | **str** |  | [optional] 
**repository_name** | **str** |  | [optional] 
**upload_session_id** | **UUID** |  | [optional] 
**authorized_scope** | **str** |  | [optional] 
**file_content_hash** | **str** | Lowercase 64-character BLAKE3 hash of the complete unencoded file bytes. | [optional] 
**expected_size** | **int** |  | [optional] 
**chunking_suite_id** | **str** | Versioned chunking suite identifier. | [optional] 
**sampling_policy_snapshot** | **str** |  | [optional] 
**operation_id** | **str** | Caller-supplied idempotency key for one upload-session operation. | [optional] 

## Example

```python
from grace_generated_openapi_probe.models.start_manifest_upload_session_parameters import StartManifestUploadSessionParameters

# TODO update the JSON string below
json = "{}"
# create an instance of StartManifestUploadSessionParameters from a JSON string
start_manifest_upload_session_parameters_instance = StartManifestUploadSessionParameters.from_json(json)
# print the JSON string representation of the object
print(StartManifestUploadSessionParameters.to_json())

# convert the object into a dict
start_manifest_upload_session_parameters_dict = start_manifest_upload_session_parameters_instance.to_dict()
# create an instance of StartManifestUploadSessionParameters from a dict
start_manifest_upload_session_parameters_from_dict = StartManifestUploadSessionParameters.from_dict(start_manifest_upload_session_parameters_dict)
```
[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


