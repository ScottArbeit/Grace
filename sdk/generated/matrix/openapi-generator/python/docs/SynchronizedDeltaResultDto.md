# SynchronizedDeltaResultDto


## Properties

Name | Type | Description | Notes
------------ | ------------- | ------------- | -------------
**outcome** | [**SynchronizedOutcomeKind**](SynchronizedOutcomeKind.md) |  | 
**cursor_epoch** | **str** | Opaque repository epoch. Clients compare only exact equality. | 
**mutations** | [**List[SynchronizedMutationDto]**](SynchronizedMutationDto.md) |  | 
**last_cursor** | **str** | Opaque repository cursor. Clients must not parse or compare its contents. | 
**has_more** | **bool** |  | 
**next_page_token** | **str** | Opaque token for continuing one immutable page sequence. | 
**rebaseline** | [**SynchronizedRebaselineDto**](SynchronizedRebaselineDto.md) |  | 

## Example

```python
from grace_generated_openapi_probe.models.synchronized_delta_result_dto import SynchronizedDeltaResultDto

# TODO update the JSON string below
json = "{}"
# create an instance of SynchronizedDeltaResultDto from a JSON string
synchronized_delta_result_dto_instance = SynchronizedDeltaResultDto.from_json(json)
# print the JSON string representation of the object
print(SynchronizedDeltaResultDto.to_json())

# convert the object into a dict
synchronized_delta_result_dto_dict = synchronized_delta_result_dto_instance.to_dict()
# create an instance of SynchronizedDeltaResultDto from a dict
synchronized_delta_result_dto_from_dict = SynchronizedDeltaResultDto.from_dict(synchronized_delta_result_dto_dict)
```
[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


