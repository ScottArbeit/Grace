# LibraryCatalogChangeResultDto


## Properties

Name | Type | Description | Notes
------------ | ------------- | ------------- | -------------
**operation_id** | **UUID** |  | 
**outcome** | [**LibraryOutcomeKind**](LibraryOutcomeKind.md) |  | 
**library_catalog** | [**LibraryCatalogDto**](LibraryCatalogDto.md) |  | 
**reason_code** | **str** |  | 
**recorded_at** | **datetime** |  | 

## Example

```python
from grace_generated_openapi_probe.models.library_catalog_change_result_dto import LibraryCatalogChangeResultDto

# TODO update the JSON string below
json = "{}"
# create an instance of LibraryCatalogChangeResultDto from a JSON string
library_catalog_change_result_dto_instance = LibraryCatalogChangeResultDto.from_json(json)
# print the JSON string representation of the object
print(LibraryCatalogChangeResultDto.to_json())

# convert the object into a dict
library_catalog_change_result_dto_dict = library_catalog_change_result_dto_instance.to_dict()
# create an instance of LibraryCatalogChangeResultDto from a dict
library_catalog_change_result_dto_from_dict = LibraryCatalogChangeResultDto.from_dict(library_catalog_change_result_dto_dict)
```
[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


