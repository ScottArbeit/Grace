# LibraryCreationSlotExpectationDto


## Properties

Name | Type | Description | Notes
------------ | ------------- | ------------- | -------------
**parent** | [**LibraryParentDto**](LibraryParentDto.md) |  | 
**name** | **str** |  | 
**expected_slot_version** | **UUID** |  | 
**expected_state** | **str** |  | 

## Example

```python
from grace_generated_openapi_probe.models.library_creation_slot_expectation_dto import LibraryCreationSlotExpectationDto

# TODO update the JSON string below
json = "{}"
# create an instance of LibraryCreationSlotExpectationDto from a JSON string
library_creation_slot_expectation_dto_instance = LibraryCreationSlotExpectationDto.from_json(json)
# print the JSON string representation of the object
print(LibraryCreationSlotExpectationDto.to_json())

# convert the object into a dict
library_creation_slot_expectation_dto_dict = library_creation_slot_expectation_dto_instance.to_dict()
# create an instance of LibraryCreationSlotExpectationDto from a dict
library_creation_slot_expectation_dto_from_dict = LibraryCreationSlotExpectationDto.from_dict(library_creation_slot_expectation_dto_dict)
```
[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


