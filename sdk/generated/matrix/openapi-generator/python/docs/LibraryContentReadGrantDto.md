# LibraryContentReadGrantDto


## Properties

Name | Type | Description | Notes
------------ | ------------- | ------------- | -------------
**grant_id** | **str** |  | 
**download_path** | **str** |  | 
**content** | [**LibraryContentVersionDto**](LibraryContentVersionDto.md) |  | 
**expires_at** | **datetime** |  | 

## Example

```python
from grace_generated_openapi_probe.models.library_content_read_grant_dto import LibraryContentReadGrantDto

# TODO update the JSON string below
json = "{}"
# create an instance of LibraryContentReadGrantDto from a JSON string
library_content_read_grant_dto_instance = LibraryContentReadGrantDto.from_json(json)
# print the JSON string representation of the object
print(LibraryContentReadGrantDto.to_json())

# convert the object into a dict
library_content_read_grant_dto_dict = library_content_read_grant_dto_instance.to_dict()
# create an instance of LibraryContentReadGrantDto from a dict
library_content_read_grant_dto_from_dict = LibraryContentReadGrantDto.from_dict(library_content_read_grant_dto_dict)
```
[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


