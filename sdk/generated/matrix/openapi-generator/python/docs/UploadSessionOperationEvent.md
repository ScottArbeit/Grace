# UploadSessionOperationEvent

Event cases whose payload is only an operation id.

## Properties

Name | Type | Description | Notes
------------ | ------------- | ------------- | -------------
**event_case** | **str** |  | 
**operation_id** | **str** | Caller-supplied idempotency key for one upload-session operation. | 

## Example

```python
from grace_generated_openapi_probe.models.upload_session_operation_event import UploadSessionOperationEvent

# TODO update the JSON string below
json = "{}"
# create an instance of UploadSessionOperationEvent from a JSON string
upload_session_operation_event_instance = UploadSessionOperationEvent.from_json(json)
# print the JSON string representation of the object
print(UploadSessionOperationEvent.to_json())

# convert the object into a dict
upload_session_operation_event_dict = upload_session_operation_event_instance.to_dict()
# create an instance of UploadSessionOperationEvent from a dict
upload_session_operation_event_from_dict = UploadSessionOperationEvent.from_dict(upload_session_operation_event_dict)
```
[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


