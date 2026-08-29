# LibraryRebaselineDto


## Properties

Name | Type | Description | Notes
------------ | ------------- | ------------- | -------------
**reason** | **str** |  | 
**current_epoch** | **str** | Opaque repository epoch. Clients compare only exact equality. | 
**service_floor_cursor** | **str** | Opaque repository cursor. Clients must not parse or compare its contents. | 
**recommended_bootstrap** | **bool** |  | 

## Example

```python
from grace_generated_openapi_probe.models.library_rebaseline_dto import LibraryRebaselineDto

# TODO update the JSON string below
json = "{}"
# create an instance of LibraryRebaselineDto from a JSON string
library_rebaseline_dto_instance = LibraryRebaselineDto.from_json(json)
# print the JSON string representation of the object
print(LibraryRebaselineDto.to_json())

# convert the object into a dict
library_rebaseline_dto_dict = library_rebaseline_dto_instance.to_dict()
# create an instance of LibraryRebaselineDto from a dict
library_rebaseline_dto_from_dict = LibraryRebaselineDto.from_dict(library_rebaseline_dto_dict)
```
[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


