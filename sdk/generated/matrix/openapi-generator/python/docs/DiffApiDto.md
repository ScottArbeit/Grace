# DiffApiDto

Public diff DTO returned through diff endpoints.

## Properties

Name | Type | Description | Notes
------------ | ------------- | ------------- | -------------
**var_class** | **str** |  | [optional] 
**owner_id** | **UUID** |  | [optional] 
**organization_id** | **UUID** |  | [optional] 
**repository_id** | **UUID** |  | [optional] 
**directory_version_id1** | **UUID** |  | [optional] 
**directory1_created_at** | **datetime** |  | [optional] 
**directory_version_id2** | **UUID** |  | [optional] 
**directory2_created_at** | **datetime** |  | [optional] 
**has_differences** | **bool** |  | [optional] 
**differences** | [**List[FileSystemDifference]**](FileSystemDifference.md) |  | [optional] 
**file_diffs** | [**List[FileDiff]**](FileDiff.md) |  | [optional] 

## Example

```python
from grace_generated_openapi_probe.models.diff_api_dto import DiffApiDto

# TODO update the JSON string below
json = "{}"
# create an instance of DiffApiDto from a JSON string
diff_api_dto_instance = DiffApiDto.from_json(json)
# print the JSON string representation of the object
print(DiffApiDto.to_json())

# convert the object into a dict
diff_api_dto_dict = diff_api_dto_instance.to_dict()
# create an instance of DiffApiDto from a dict
diff_api_dto_from_dict = DiffApiDto.from_dict(diff_api_dto_dict)
```
[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


