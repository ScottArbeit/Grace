# LibraryNamespaceDto


## Properties

Name | Type | Description | Notes
------------ | ------------- | ------------- | -------------
**parent** | [**LibraryParentDto**](LibraryParentDto.md) |  | 
**name** | **str** |  | 
**normalized_path** | **str** |  | 
**namespace_version** | **UUID** |  | 
**slot_version** | **UUID** |  | 

## Example

```python
from grace_generated_openapi_probe.models.library_namespace_dto import LibraryNamespaceDto

# TODO update the JSON string below
json = "{}"
# create an instance of LibraryNamespaceDto from a JSON string
library_namespace_dto_instance = LibraryNamespaceDto.from_json(json)
# print the JSON string representation of the object
print(LibraryNamespaceDto.to_json())

# convert the object into a dict
library_namespace_dto_dict = library_namespace_dto_instance.to_dict()
# create an instance of LibraryNamespaceDto from a dict
library_namespace_dto_from_dict = LibraryNamespaceDto.from_dict(library_namespace_dto_dict)
```
[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


