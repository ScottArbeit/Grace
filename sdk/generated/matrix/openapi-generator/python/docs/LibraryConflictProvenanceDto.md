# LibraryConflictProvenanceDto


## Properties

Name | Type | Description | Notes
------------ | ------------- | ------------- | -------------
**original_item_id** | **UUID** |  | 
**base_content_version_id** | **UUID** |  | 
**base_content_revision** | **str** | Opaque repository cursor. Clients must not parse or compare its contents. | 

## Example

```python
from grace_generated_openapi_probe.models.library_conflict_provenance_dto import LibraryConflictProvenanceDto

# TODO update the JSON string below
json = "{}"
# create an instance of LibraryConflictProvenanceDto from a JSON string
library_conflict_provenance_dto_instance = LibraryConflictProvenanceDto.from_json(json)
# print the JSON string representation of the object
print(LibraryConflictProvenanceDto.to_json())

# convert the object into a dict
library_conflict_provenance_dto_dict = library_conflict_provenance_dto_instance.to_dict()
# create an instance of LibraryConflictProvenanceDto from a dict
library_conflict_provenance_dto_from_dict = LibraryConflictProvenanceDto.from_dict(library_conflict_provenance_dto_dict)
```
[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


