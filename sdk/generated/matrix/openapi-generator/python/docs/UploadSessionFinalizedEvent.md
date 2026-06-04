# UploadSessionFinalizedEvent


## Properties

Name | Type | Description | Notes
------------ | ------------- | ------------- | -------------
**finalized** | [**UploadSessionFinalizedEventFinalized**](UploadSessionFinalizedEventFinalized.md) |  | 

## Example

```python
from grace_generated_openapi_probe.models.upload_session_finalized_event import UploadSessionFinalizedEvent

# TODO update the JSON string below
json = "{}"
# create an instance of UploadSessionFinalizedEvent from a JSON string
upload_session_finalized_event_instance = UploadSessionFinalizedEvent.from_json(json)
# print the JSON string representation of the object
print(UploadSessionFinalizedEvent.to_json())

# convert the object into a dict
upload_session_finalized_event_dict = upload_session_finalized_event_instance.to_dict()
# create an instance of UploadSessionFinalizedEvent from a dict
upload_session_finalized_event_from_dict = UploadSessionFinalizedEvent.from_dict(upload_session_finalized_event_dict)
```
[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


