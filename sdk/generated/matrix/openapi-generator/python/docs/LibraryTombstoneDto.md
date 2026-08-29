# LibraryTombstoneDto


## Properties

Name | Type | Description | Notes
------------ | ------------- | ------------- | -------------
**item_id** | **UUID** |  | 
**item_kind** | [**LibraryItemKind**](LibraryItemKind.md) |  | 
**deleted_at** | **datetime** |  | 
**deleted_by** | **str** |  | 
**delete_cursor** | **str** | Opaque repository cursor. Clients must not parse or compare its contents. | 
**last_namespace_version** | **UUID** |  | 
**last_content_version_id** | **UUID** |  | 

## Example

```python
from grace_generated_openapi_probe.models.library_tombstone_dto import LibraryTombstoneDto

# TODO update the JSON string below
json = "{}"
# create an instance of LibraryTombstoneDto from a JSON string
library_tombstone_dto_instance = LibraryTombstoneDto.from_json(json)
# print the JSON string representation of the object
print(LibraryTombstoneDto.to_json())

# convert the object into a dict
library_tombstone_dto_dict = library_tombstone_dto_instance.to_dict()
# create an instance of LibraryTombstoneDto from a dict
library_tombstone_dto_from_dict = LibraryTombstoneDto.from_dict(library_tombstone_dto_dict)
```
[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


