# UploadSessionFinalizedEventFinalized


## Properties

Name | Type | Description | Notes
------------ | ------------- | ------------- | -------------
**operation_id** | **str** | Caller-supplied idempotency key for one upload-session operation. | 
**manifest_address** | **str** | Lowercase 64-character BLAKE3-derived FileManifest address. | 

## Example

```python
from grace_generated_openapi_probe.models.upload_session_finalized_event_finalized import UploadSessionFinalizedEventFinalized

# TODO update the JSON string below
json = "{}"
# create an instance of UploadSessionFinalizedEventFinalized from a JSON string
upload_session_finalized_event_finalized_instance = UploadSessionFinalizedEventFinalized.from_json(json)
# print the JSON string representation of the object
print(UploadSessionFinalizedEventFinalized.to_json())

# convert the object into a dict
upload_session_finalized_event_finalized_dict = upload_session_finalized_event_finalized_instance.to_dict()
# create an instance of UploadSessionFinalizedEventFinalized from a dict
upload_session_finalized_event_finalized_from_dict = UploadSessionFinalizedEventFinalized.from_dict(upload_session_finalized_event_finalized_dict)
```
[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


