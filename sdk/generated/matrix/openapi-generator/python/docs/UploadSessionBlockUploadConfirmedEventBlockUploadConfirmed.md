# UploadSessionBlockUploadConfirmedEventBlockUploadConfirmed


## Properties

Name | Type | Description | Notes
------------ | ------------- | ------------- | -------------
**operation_id** | **str** | Caller-supplied idempotency key for one upload-session operation. | 
**confirmed_block** | [**ConfirmedBlockUpload**](ConfirmedBlockUpload.md) |  | 

## Example

```python
from grace_generated_openapi_probe.models.upload_session_block_upload_confirmed_event_block_upload_confirmed import UploadSessionBlockUploadConfirmedEventBlockUploadConfirmed

# TODO update the JSON string below
json = "{}"
# create an instance of UploadSessionBlockUploadConfirmedEventBlockUploadConfirmed from a JSON string
upload_session_block_upload_confirmed_event_block_upload_confirmed_instance = UploadSessionBlockUploadConfirmedEventBlockUploadConfirmed.from_json(json)
# print the JSON string representation of the object
print(UploadSessionBlockUploadConfirmedEventBlockUploadConfirmed.to_json())

# convert the object into a dict
upload_session_block_upload_confirmed_event_block_upload_confirmed_dict = upload_session_block_upload_confirmed_event_block_upload_confirmed_instance.to_dict()
# create an instance of UploadSessionBlockUploadConfirmedEventBlockUploadConfirmed from a dict
upload_session_block_upload_confirmed_event_block_upload_confirmed_from_dict = UploadSessionBlockUploadConfirmedEventBlockUploadConfirmed.from_dict(upload_session_block_upload_confirmed_event_block_upload_confirmed_dict)
```
[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


