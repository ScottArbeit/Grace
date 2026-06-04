# UploadSessionBlockUploadIntentRegisteredEventBlockUploadIntentRegistered


## Properties

Name | Type | Description | Notes
------------ | ------------- | ------------- | -------------
**operation_id** | **str** | Caller-supplied idempotency key for one upload-session operation. | 
**intent** | [**BlockUploadIntent**](BlockUploadIntent.md) |  | 

## Example

```python
from grace_generated_openapi_probe.models.upload_session_block_upload_intent_registered_event_block_upload_intent_registered import UploadSessionBlockUploadIntentRegisteredEventBlockUploadIntentRegistered

# TODO update the JSON string below
json = "{}"
# create an instance of UploadSessionBlockUploadIntentRegisteredEventBlockUploadIntentRegistered from a JSON string
upload_session_block_upload_intent_registered_event_block_upload_intent_registered_instance = UploadSessionBlockUploadIntentRegisteredEventBlockUploadIntentRegistered.from_json(json)
# print the JSON string representation of the object
print(UploadSessionBlockUploadIntentRegisteredEventBlockUploadIntentRegistered.to_json())

# convert the object into a dict
upload_session_block_upload_intent_registered_event_block_upload_intent_registered_dict = upload_session_block_upload_intent_registered_event_block_upload_intent_registered_instance.to_dict()
# create an instance of UploadSessionBlockUploadIntentRegisteredEventBlockUploadIntentRegistered from a dict
upload_session_block_upload_intent_registered_event_block_upload_intent_registered_from_dict = UploadSessionBlockUploadIntentRegisteredEventBlockUploadIntentRegistered.from_dict(upload_session_block_upload_intent_registered_event_block_upload_intent_registered_dict)
```
[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


