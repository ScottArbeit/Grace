# DirectoryVersionApiDto

Public directory version DTO returned by directory query endpoints.

## Properties

Name | Type | Description | Notes
------------ | ------------- | ------------- | -------------
**directory_version** | [**DirectoryVersion**](DirectoryVersion.md) |  | [optional] 
**recursive_size** | **int** |  | [optional] 
**deleted_at** | **datetime** |  | [optional] 
**delete_reason** | **str** |  | [optional] 
**hashes_validated** | **bool** |  | [optional] 

## Example

```python
from grace_generated_openapi_probe.models.directory_version_api_dto import DirectoryVersionApiDto

# TODO update the JSON string below
json = "{}"
# create an instance of DirectoryVersionApiDto from a JSON string
directory_version_api_dto_instance = DirectoryVersionApiDto.from_json(json)
# print the JSON string representation of the object
print(DirectoryVersionApiDto.to_json())

# convert the object into a dict
directory_version_api_dto_dict = directory_version_api_dto_instance.to_dict()
# create an instance of DirectoryVersionApiDto from a dict
directory_version_api_dto_from_dict = DirectoryVersionApiDto.from_dict(directory_version_api_dto_dict)
```
[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


