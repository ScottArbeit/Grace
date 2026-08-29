# LibraryPreparedContentDto


## Properties

Name | Type | Description | Notes
------------ | ------------- | ------------- | -------------
**prepared_content_id** | **UUID** |  | 
**blake3_hash** | **str** | Lowercase 64-character BLAKE3 version hash persisted on new version graph DTOs. | 
**sha256_hash** | **str** | Lowercase 64-character SHA-256 version hash persisted on version DTOs. | 
**size** | **int** |  | 
**upload_required** | **bool** |  | 
**upload_instructions** | **str** |  | 
**expires_at** | **datetime** |  | 

## Example

```python
from grace_generated_openapi_probe.models.library_prepared_content_dto import LibraryPreparedContentDto

# TODO update the JSON string below
json = "{}"
# create an instance of LibraryPreparedContentDto from a JSON string
library_prepared_content_dto_instance = LibraryPreparedContentDto.from_json(json)
# print the JSON string representation of the object
print(LibraryPreparedContentDto.to_json())

# convert the object into a dict
library_prepared_content_dto_dict = library_prepared_content_dto_instance.to_dict()
# create an instance of LibraryPreparedContentDto from a dict
library_prepared_content_dto_from_dict = LibraryPreparedContentDto.from_dict(library_prepared_content_dto_dict)
```
[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


