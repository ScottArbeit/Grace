# LibraryItemDto


## Properties

Name | Type | Description | Notes
------------ | ------------- | ------------- | -------------
**item_id** | **UUID** |  | 
**item_kind** | [**LibraryItemKind**](LibraryItemKind.md) |  | 
**state** | **str** |  | 
**last_change_cursor** | **str** | Opaque repository cursor. Clients must not parse or compare its contents. | 
**library_catalog_version** | **UUID** |  | 
**namespace** | [**LibraryNamespaceDto**](LibraryNamespaceDto.md) |  | 
**content** | [**LibraryContentVersionDto**](LibraryContentVersionDto.md) |  | 
**tombstone** | [**LibraryTombstoneDto**](LibraryTombstoneDto.md) |  | 

## Example

```python
from grace_generated_openapi_probe.models.library_item_dto import LibraryItemDto

# TODO update the JSON string below
json = "{}"
# create an instance of LibraryItemDto from a JSON string
library_item_dto_instance = LibraryItemDto.from_json(json)
# print the JSON string representation of the object
print(LibraryItemDto.to_json())

# convert the object into a dict
library_item_dto_dict = library_item_dto_instance.to_dict()
# create an instance of LibraryItemDto from a dict
library_item_dto_from_dict = LibraryItemDto.from_dict(library_item_dto_dict)
```
[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


