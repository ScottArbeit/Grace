# ReferenceReplayApiDto

Eligible Reference events and the exact closure of one immutable branch-event snapshot.

## Properties

Name | Type | Description | Notes
------------ | ------------- | ------------- | -------------
**repository_id** | **UUID** |  | 
**branch_id** | **UUID** |  | 
**events** | [**List[ReferenceReplayEventApiDto]**](ReferenceReplayEventApiDto.md) |  | 
**scanned_through_cursor** | **str** | Opaque cursor closing the complete response interval; clients must not interpret or modify it. | 

## Example

```python
from grace_generated_openapi_probe.models.reference_replay_api_dto import ReferenceReplayApiDto

# TODO update the JSON string below
json = "{}"
# create an instance of ReferenceReplayApiDto from a JSON string
reference_replay_api_dto_instance = ReferenceReplayApiDto.from_json(json)
# print the JSON string representation of the object
print(ReferenceReplayApiDto.to_json())

# convert the object into a dict
reference_replay_api_dto_dict = reference_replay_api_dto_instance.to_dict()
# create an instance of ReferenceReplayApiDto from a dict
reference_replay_api_dto_from_dict = ReferenceReplayApiDto.from_dict(reference_replay_api_dto_dict)
```
[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


