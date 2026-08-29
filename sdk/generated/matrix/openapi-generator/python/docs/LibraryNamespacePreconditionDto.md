# LibraryNamespacePreconditionDto


## Properties

Name | Type | Description | Notes
------------ | ------------- | ------------- | -------------
**item_id** | **UUID** |  | 
**expected_namespace_version** | **UUID** |  | 

## Example

```python
from grace_generated_openapi_probe.models.library_namespace_precondition_dto import LibraryNamespacePreconditionDto

# TODO update the JSON string below
json = "{}"
# create an instance of LibraryNamespacePreconditionDto from a JSON string
library_namespace_precondition_dto_instance = LibraryNamespacePreconditionDto.from_json(json)
# print the JSON string representation of the object
print(LibraryNamespacePreconditionDto.to_json())

# convert the object into a dict
library_namespace_precondition_dto_dict = library_namespace_precondition_dto_instance.to_dict()
# create an instance of LibraryNamespacePreconditionDto from a dict
library_namespace_precondition_dto_from_dict = LibraryNamespacePreconditionDto.from_dict(library_namespace_precondition_dto_dict)
```
[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


