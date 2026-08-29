# LibraryCatalogDto


## Properties

Name | Type | Description | Notes
------------ | ------------- | ------------- | -------------
**repository_id** | **UUID** |  | 
**version** | **UUID** |  | 
**libraries** | **List[str]** |  | 
**created_at** | **datetime** |  | 
**created_by** | **str** |  | 
**previous_version** | **UUID** |  | 

## Example

```python
from grace_generated_openapi_probe.models.library_catalog_dto import LibraryCatalogDto

# TODO update the JSON string below
json = "{}"
# create an instance of LibraryCatalogDto from a JSON string
library_catalog_dto_instance = LibraryCatalogDto.from_json(json)
# print the JSON string representation of the object
print(LibraryCatalogDto.to_json())

# convert the object into a dict
library_catalog_dto_dict = library_catalog_dto_instance.to_dict()
# create an instance of LibraryCatalogDto from a dict
library_catalog_dto_from_dict = LibraryCatalogDto.from_dict(library_catalog_dto_dict)
```
[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


