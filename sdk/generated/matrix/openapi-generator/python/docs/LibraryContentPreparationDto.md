# LibraryContentPreparationDto


## Properties

Name | Type | Description | Notes
------------ | ------------- | ------------- | -------------
**upload_session_id** | **UUID** |  | 
**blake3_hash** | **str** | Lowercase 64-character BLAKE3 version hash persisted on new version graph DTOs. | 
**sha256_hash** | **str** | Lowercase 64-character SHA-256 version hash persisted on version DTOs. | 
**size** | **int** |  | 
**authorized_scope** | **str** |  | 
**storage_pool_id** | **str** |  | 
**expires_at** | **datetime** |  | 

## Example

```python
from grace_generated_openapi_probe.models.library_content_preparation_dto import LibraryContentPreparationDto

# TODO update the JSON string below
json = "{}"
# create an instance of LibraryContentPreparationDto from a JSON string
library_content_preparation_dto_instance = LibraryContentPreparationDto.from_json(json)
# print the JSON string representation of the object
print(LibraryContentPreparationDto.to_json())

# convert the object into a dict
library_content_preparation_dto_dict = library_content_preparation_dto_instance.to_dict()
# create an instance of LibraryContentPreparationDto from a dict
library_content_preparation_dto_from_dict = LibraryContentPreparationDto.from_dict(library_content_preparation_dto_dict)
```
[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


