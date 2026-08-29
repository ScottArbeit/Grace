# LibraryBootstrapPageDto


## Properties

Name | Type | Description | Notes
------------ | ------------- | ------------- | -------------
**bootstrap_id** | **UUID** |  | 
**boundary_cursor** | **str** | Opaque repository cursor. Clients must not parse or compare its contents. | 
**cursor_epoch** | **str** | Opaque repository epoch. Clients compare only exact equality. | 
**library_catalog** | [**LibraryCatalogDto**](LibraryCatalogDto.md) |  | 
**items** | [**List[LibraryItemDto]**](LibraryItemDto.md) |  | 
**next_page_token** | **str** | Opaque token for continuing one immutable page sequence. | 

## Example

```python
from grace_generated_openapi_probe.models.library_bootstrap_page_dto import LibraryBootstrapPageDto

# TODO update the JSON string below
json = "{}"
# create an instance of LibraryBootstrapPageDto from a JSON string
library_bootstrap_page_dto_instance = LibraryBootstrapPageDto.from_json(json)
# print the JSON string representation of the object
print(LibraryBootstrapPageDto.to_json())

# convert the object into a dict
library_bootstrap_page_dto_dict = library_bootstrap_page_dto_instance.to_dict()
# create an instance of LibraryBootstrapPageDto from a dict
library_bootstrap_page_dto_from_dict = LibraryBootstrapPageDto.from_dict(library_bootstrap_page_dto_dict)
```
[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


