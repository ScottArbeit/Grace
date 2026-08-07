# ReferenceReplayEventApiDto

One eligible Reference event paired with its opaque durable branch-event cursor.

## Properties

Name | Type | Description | Notes
------------ | ------------- | ------------- | -------------
**event_cursor** | **str** | Opaque cursor interpreted only by Grace Server. | 
**reference** | [**CurrentBranchReferenceNotification**](CurrentBranchReferenceNotification.md) |  | 

## Example

```python
from grace_generated_openapi_probe.models.reference_replay_event_api_dto import ReferenceReplayEventApiDto

# TODO update the JSON string below
json = "{}"
# create an instance of ReferenceReplayEventApiDto from a JSON string
reference_replay_event_api_dto_instance = ReferenceReplayEventApiDto.from_json(json)
# print the JSON string representation of the object
print(ReferenceReplayEventApiDto.to_json())

# convert the object into a dict
reference_replay_event_api_dto_dict = reference_replay_event_api_dto_instance.to_dict()
# create an instance of ReferenceReplayEventApiDto from a dict
reference_replay_event_api_dto_from_dict = ReferenceReplayEventApiDto.from_dict(reference_replay_event_api_dto_dict)
```
[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


