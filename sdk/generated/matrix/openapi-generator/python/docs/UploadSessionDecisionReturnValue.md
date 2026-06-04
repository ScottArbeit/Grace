# UploadSessionDecisionReturnValue

Successful upload-session command response.

## Properties

Name | Type | Description | Notes
------------ | ------------- | ------------- | -------------
**event_time** | **datetime** |  | 
**correlation_id** | **str** |  | 
**properties** | **Dict[str, object]** |  | 
**return_value** | [**UploadSessionDecision**](UploadSessionDecision.md) |  | 

## Example

```python
from grace_generated_openapi_probe.models.upload_session_decision_return_value import UploadSessionDecisionReturnValue

# TODO update the JSON string below
json = "{}"
# create an instance of UploadSessionDecisionReturnValue from a JSON string
upload_session_decision_return_value_instance = UploadSessionDecisionReturnValue.from_json(json)
# print the JSON string representation of the object
print(UploadSessionDecisionReturnValue.to_json())

# convert the object into a dict
upload_session_decision_return_value_dict = upload_session_decision_return_value_instance.to_dict()
# create an instance of UploadSessionDecisionReturnValue from a dict
upload_session_decision_return_value_from_dict = UploadSessionDecisionReturnValue.from_dict(upload_session_decision_return_value_dict)
```
[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


