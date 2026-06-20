# StartUploadSession

Event payload recorded when a manifest upload session starts.

## Properties

Name | Type | Description | Notes
------------ | ------------- | ------------- | -------------
**upload_session_id** | **UUID** |  | 
**owner_id** | **UUID** |  | 
**organization_id** | **UUID** |  | 
**repository_id** | **UUID** |  | 
**storage_pool_id** | **str** | StoragePool-wide CAS scope identifier. | 
**authorized_scope** | **str** |  | 
**file_content_hash** | **str** | Lowercase 64-character BLAKE3 hash of the complete unencoded file bytes. | 
**expected_size** | **int** |  | 
**chunking_suite_id** | **str** | Versioned chunking suite identifier. | 
**sampling_policy_snapshot** | **str** |  | 
**operation_id** | **str** | Caller-supplied idempotency key for one upload-session operation. | 

## Example

```python
from grace_generated_openapi_probe.models.start_upload_session import StartUploadSession

# TODO update the JSON string below
json = "{}"
# create an instance of StartUploadSession from a JSON string
start_upload_session_instance = StartUploadSession.from_json(json)
# print the JSON string representation of the object
print(StartUploadSession.to_json())

# convert the object into a dict
start_upload_session_dict = start_upload_session_instance.to_dict()
# create an instance of StartUploadSession from a dict
start_upload_session_from_dict = StartUploadSession.from_dict(start_upload_session_dict)
```
[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


