# LibraryNamespaceSlotDto


## Properties

Name | Type | Description | Notes
------------ | ------------- | ------------- | -------------
**parent** | [**LibraryParentDto**](LibraryParentDto.md) |  | 
**name** | **str** |  | 
**normalized_path** | **str** |  | 
**slot_version** | **UUID** |  | 
**state** | **str** |  | 
**occupant_item_id** | **UUID** |  | 

## Example

```python
from grace_generated_openapi_probe.models.library_namespace_slot_dto import LibraryNamespaceSlotDto

# TODO update the JSON string below
json = "{}"
# create an instance of LibraryNamespaceSlotDto from a JSON string
library_namespace_slot_dto_instance = LibraryNamespaceSlotDto.from_json(json)
# print the JSON string representation of the object
print(LibraryNamespaceSlotDto.to_json())

# convert the object into a dict
library_namespace_slot_dto_dict = library_namespace_slot_dto_instance.to_dict()
# create an instance of LibraryNamespaceSlotDto from a dict
library_namespace_slot_dto_from_dict = LibraryNamespaceSlotDto.from_dict(library_namespace_slot_dto_dict)
```
[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


