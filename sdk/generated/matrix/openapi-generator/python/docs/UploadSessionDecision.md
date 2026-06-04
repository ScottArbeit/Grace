# UploadSessionDecision

UploadSession command decision returned by manifest upload-session endpoints.

## Properties

Name | Type | Description | Notes
------------ | ------------- | ------------- | -------------
**session** | [**UploadSessionDto**](UploadSessionDto.md) |  | 
**operation_id** | **str** | Caller-supplied idempotency key for one upload-session operation. | 
**events** | [**List[UploadSessionEvent]**](UploadSessionEvent.md) |  | 
**was_idempotent_replay** | **bool** |  | 
**message** | **str** |  | 

## Example

```python
from grace_generated_openapi_probe.models.upload_session_decision import UploadSessionDecision

# TODO update the JSON string below
json = "{}"
# create an instance of UploadSessionDecision from a JSON string
upload_session_decision_instance = UploadSessionDecision.from_json(json)
# print the JSON string representation of the object
print(UploadSessionDecision.to_json())

# convert the object into a dict
upload_session_decision_dict = upload_session_decision_instance.to_dict()
# create an instance of UploadSessionDecision from a dict
upload_session_decision_from_dict = UploadSessionDecision.from_dict(upload_session_decision_dict)
```
[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


