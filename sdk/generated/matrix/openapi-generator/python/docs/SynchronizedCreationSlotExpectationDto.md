# SynchronizedCreationSlotExpectationDto


## Properties

Name | Type | Description | Notes
------------ | ------------- | ------------- | -------------
**parent** | [**SynchronizedParentDto**](SynchronizedParentDto.md) |  | 
**name** | **str** |  | 
**expected_slot_version** | **UUID** |  | 
**expected_state** | **str** |  | 

## Example

```python
from grace_generated_openapi_probe.models.synchronized_creation_slot_expectation_dto import SynchronizedCreationSlotExpectationDto

# TODO update the JSON string below
json = "{}"
# create an instance of SynchronizedCreationSlotExpectationDto from a JSON string
synchronized_creation_slot_expectation_dto_instance = SynchronizedCreationSlotExpectationDto.from_json(json)
# print the JSON string representation of the object
print(SynchronizedCreationSlotExpectationDto.to_json())

# convert the object into a dict
synchronized_creation_slot_expectation_dto_dict = synchronized_creation_slot_expectation_dto_instance.to_dict()
# create an instance of SynchronizedCreationSlotExpectationDto from a dict
synchronized_creation_slot_expectation_dto_from_dict = SynchronizedCreationSlotExpectationDto.from_dict(synchronized_creation_slot_expectation_dto_dict)
```
[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


