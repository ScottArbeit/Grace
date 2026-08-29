# LibraryContentVersionDto


## Properties

Name | Type | Description | Notes
------------ | ------------- | ------------- | -------------
**content_version_id** | **UUID** |  | 
**blake3_hash** | **str** | Lowercase 64-character BLAKE3 version hash persisted on new version graph DTOs. | 
**sha256_hash** | **str** | Lowercase 64-character SHA-256 version hash persisted on version DTOs. | 
**size** | **int** |  | 
**created_at** | **datetime** |  | 

## Example

```python
from grace_generated_openapi_probe.models.library_content_version_dto import LibraryContentVersionDto

# TODO update the JSON string below
json = "{}"
# create an instance of LibraryContentVersionDto from a JSON string
library_content_version_dto_instance = LibraryContentVersionDto.from_json(json)
# print the JSON string representation of the object
print(LibraryContentVersionDto.to_json())

# convert the object into a dict
library_content_version_dto_dict = library_content_version_dto_instance.to_dict()
# create an instance of LibraryContentVersionDto from a dict
library_content_version_dto_from_dict = LibraryContentVersionDto.from_dict(library_content_version_dto_dict)
```
[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


