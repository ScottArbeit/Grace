# LibraryChangeDto


## Properties

Name | Type | Description | Notes
------------ | ------------- | ------------- | -------------
**cursor** | **str** | Opaque repository cursor. Clients must not parse or compare its contents. | 
**operation_id** | **UUID** |  | 
**change_kind** | [**LibraryChangeKind**](LibraryChangeKind.md) |  | 
**item_id** | **UUID** |  | 
**item_kind** | [**LibraryItemKind**](LibraryItemKind.md) |  | 
**accepted_at** | **datetime** |  | 
**accepted_by** | **str** |  | 
**library_catalog_version** | **UUID** |  | 
**namespace** | [**LibraryNamespaceDto**](LibraryNamespaceDto.md) |  | 
**content** | [**LibraryContentVersionDto**](LibraryContentVersionDto.md) |  | 
**tombstone** | [**LibraryTombstoneDto**](LibraryTombstoneDto.md) |  | 
**conflict** | [**LibraryConflictProvenanceDto**](LibraryConflictProvenanceDto.md) |  | 

## Example

```python
from grace_generated_openapi_probe.models.library_change_dto import LibraryChangeDto

# TODO update the JSON string below
json = "{}"
# create an instance of LibraryChangeDto from a JSON string
library_change_dto_instance = LibraryChangeDto.from_json(json)
# print the JSON string representation of the object
print(LibraryChangeDto.to_json())

# convert the object into a dict
library_change_dto_dict = library_change_dto_instance.to_dict()
# create an instance of LibraryChangeDto from a dict
library_change_dto_from_dict = LibraryChangeDto.from_dict(library_change_dto_dict)
```
[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


