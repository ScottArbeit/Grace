# DiffDto

(automatically generated)

## Properties

Name | Type | Description | Notes
------------ | ------------- | ------------- | -------------
**var_class** | **str** |  | [optional] 
**has_differences** | **bool** |  | [optional] 
**directory_id1** | **UUID** |  | [optional] 
**directory1_created_at** | **datetime** |  | [optional] 
**directory_id2** | **UUID** |  | [optional] 
**directory2_created_at** | **datetime** |  | [optional] 
**differences** | [**List[FileSystemDifference]**](FileSystemDifference.md) |  | [optional] 
**file_diffs** | [**List[FileDiff]**](FileDiff.md) |  | [optional] 

## Example

```python
from grace_generated_openapi_probe.models.diff_dto import DiffDto

# TODO update the JSON string below
json = "{}"
# create an instance of DiffDto from a JSON string
diff_dto_instance = DiffDto.from_json(json)
# print the JSON string representation of the object
print(DiffDto.to_json())

# convert the object into a dict
diff_dto_dict = diff_dto_instance.to_dict()
# create an instance of DiffDto from a dict
diff_dto_from_dict = DiffDto.from_dict(diff_dto_dict)
```
[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


