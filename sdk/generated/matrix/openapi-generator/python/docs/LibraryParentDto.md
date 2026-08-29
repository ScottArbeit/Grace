# LibraryParentDto

Repository-owned root or directory parent identity.

## Properties

Name | Type | Description | Notes
------------ | ------------- | ------------- | -------------
**kind** | **str** |  | 
**library_path** | **str** |  | 
**item_id** | **UUID** |  | 

## Example

```python
from grace_generated_openapi_probe.models.library_parent_dto import LibraryParentDto

# TODO update the JSON string below
json = "{}"
# create an instance of LibraryParentDto from a JSON string
library_parent_dto_instance = LibraryParentDto.from_json(json)
# print the JSON string representation of the object
print(LibraryParentDto.to_json())

# convert the object into a dict
library_parent_dto_dict = library_parent_dto_instance.to_dict()
# create an instance of LibraryParentDto from a dict
library_parent_dto_from_dict = LibraryParentDto.from_dict(library_parent_dto_dict)
```
[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


