# UploadSessionBlockUploadConfirmedEvent


## Properties

Name | Type | Description | Notes
------------ | ------------- | ------------- | -------------
**block_upload_confirmed** | [**UploadSessionBlockUploadConfirmedEventBlockUploadConfirmed**](UploadSessionBlockUploadConfirmedEventBlockUploadConfirmed.md) |  | 

## Example

```python
from grace_generated_openapi_probe.models.upload_session_block_upload_confirmed_event import UploadSessionBlockUploadConfirmedEvent

# TODO update the JSON string below
json = "{}"
# create an instance of UploadSessionBlockUploadConfirmedEvent from a JSON string
upload_session_block_upload_confirmed_event_instance = UploadSessionBlockUploadConfirmedEvent.from_json(json)
# print the JSON string representation of the object
print(UploadSessionBlockUploadConfirmedEvent.to_json())

# convert the object into a dict
upload_session_block_upload_confirmed_event_dict = upload_session_block_upload_confirmed_event_instance.to_dict()
# create an instance of UploadSessionBlockUploadConfirmedEvent from a dict
upload_session_block_upload_confirmed_event_from_dict = UploadSessionBlockUploadConfirmedEvent.from_dict(upload_session_block_upload_confirmed_event_dict)
```
[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


