# UploadSessionEvent

Event emitted by the upload-session actor, paired with event metadata.

## Properties

Name | Type | Description | Notes
------------ | ------------- | ------------- | -------------
**event** | [**UploadSessionEventType**](UploadSessionEventType.md) |  | 
**metadata** | [**EventMetadata**](EventMetadata.md) |  | 

## Example

```python
from grace_generated_openapi_probe.models.upload_session_event import UploadSessionEvent

# TODO update the JSON string below
json = "{}"
# create an instance of UploadSessionEvent from a JSON string
upload_session_event_instance = UploadSessionEvent.from_json(json)
# print the JSON string representation of the object
print(UploadSessionEvent.to_json())

# convert the object into a dict
upload_session_event_dict = upload_session_event_instance.to_dict()
# create an instance of UploadSessionEvent from a dict
upload_session_event_from_dict = UploadSessionEvent.from_dict(upload_session_event_dict)
```
[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


