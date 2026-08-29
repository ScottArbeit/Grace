# LibraryContentPreconditionDto


## Properties

Name | Type | Description | Notes
------------ | ------------- | ------------- | -------------
**item_id** | **UUID** |  | 
**expected_content_version_id** | **UUID** |  | 

## Example

```python
from grace_generated_openapi_probe.models.library_content_precondition_dto import LibraryContentPreconditionDto

# TODO update the JSON string below
json = "{}"
# create an instance of LibraryContentPreconditionDto from a JSON string
library_content_precondition_dto_instance = LibraryContentPreconditionDto.from_json(json)
# print the JSON string representation of the object
print(LibraryContentPreconditionDto.to_json())

# convert the object into a dict
library_content_precondition_dto_dict = library_content_precondition_dto_instance.to_dict()
# create an instance of LibraryContentPreconditionDto from a dict
library_content_precondition_dto_from_dict = LibraryContentPreconditionDto.from_dict(library_content_precondition_dto_dict)
```
[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


