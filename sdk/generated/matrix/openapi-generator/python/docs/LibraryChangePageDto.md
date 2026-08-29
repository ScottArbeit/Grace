# LibraryChangePageDto


## Properties

Name | Type | Description | Notes
------------ | ------------- | ------------- | -------------
**outcome** | [**LibraryOutcomeKind**](LibraryOutcomeKind.md) |  | 
**cursor_epoch** | **str** | Opaque repository epoch. Clients compare only exact equality. | 
**changes** | [**List[LibraryChangeDto]**](LibraryChangeDto.md) |  | 
**last_cursor** | **str** | Opaque repository cursor. Clients must not parse or compare its contents. | 
**has_more** | **bool** |  | 
**next_page_token** | **str** | Opaque token for continuing one immutable page sequence. | 
**rebaseline** | [**LibraryRebaselineDto**](LibraryRebaselineDto.md) |  | 

## Example

```python
from grace_generated_openapi_probe.models.library_change_page_dto import LibraryChangePageDto

# TODO update the JSON string below
json = "{}"
# create an instance of LibraryChangePageDto from a JSON string
library_change_page_dto_instance = LibraryChangePageDto.from_json(json)
# print the JSON string representation of the object
print(LibraryChangePageDto.to_json())

# convert the object into a dict
library_change_page_dto_dict = library_change_page_dto_instance.to_dict()
# create an instance of LibraryChangePageDto from a dict
library_change_page_dto_from_dict = LibraryChangePageDto.from_dict(library_change_page_dto_dict)
```
[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


