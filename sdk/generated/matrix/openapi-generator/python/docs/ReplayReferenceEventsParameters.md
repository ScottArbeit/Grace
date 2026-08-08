# ReplayReferenceEventsParameters

Replays eligible Reference events after one opaque cursor for the exact branch scope that produced it.

## Properties

Name | Type | Description | Notes
------------ | ------------- | ------------- | -------------
**correlation_id** | **str** | Body DTO correlation id copied into Grace command/event metadata after request parsing. This field is distinct from the X-Correlation-Id transport header. | [optional] 
**principal** | **str** | The entity on whose behalf the action is being performed. | [optional] 
**owner_id** | **UUID** |  | [optional] 
**owner_name** | **str** |  | [optional] 
**organization_id** | **UUID** |  | [optional] 
**organization_name** | **str** |  | [optional] 
**repository_id** | **UUID** |  | [optional] 
**repository_name** | **str** |  | [optional] 
**branch_id** | **UUID** |  | [optional] 
**branch_name** | **str** |  | [optional] 
**sha256_hash** | **str** | Empty value or lowercase or uppercase 2- to 64-character SHA-256 version hash prefix. | [optional] 
**reference_id** | **UUID** |  | [optional] 
**cursor_repository_id** | **UUID** |  | 
**cursor_branch_id** | **UUID** |  | 
**event_cursor** | **str** | Opaque cursor returned by Grace Server; clients must not interpret or modify it. | 

## Example

```python
from grace_generated_openapi_probe.models.replay_reference_events_parameters import ReplayReferenceEventsParameters

# TODO update the JSON string below
json = "{}"
# create an instance of ReplayReferenceEventsParameters from a JSON string
replay_reference_events_parameters_instance = ReplayReferenceEventsParameters.from_json(json)
# print the JSON string representation of the object
print(ReplayReferenceEventsParameters.to_json())

# convert the object into a dict
replay_reference_events_parameters_dict = replay_reference_events_parameters_instance.to_dict()
# create an instance of ReplayReferenceEventsParameters from a dict
replay_reference_events_parameters_from_dict = ReplayReferenceEventsParameters.from_dict(replay_reference_events_parameters_dict)
```
[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


