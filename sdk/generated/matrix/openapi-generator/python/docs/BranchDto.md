# BranchDto

(automatically generated)

## Properties

Name | Type | Description | Notes
------------ | ------------- | ------------- | -------------
**var_class** | **str** |  | [optional] 
**branch_id** | **UUID** |  | [optional] 
**branch_name** | **str** |  | [optional] 
**parent_branch_id** | **UUID** |  | [optional] 
**based_on** | **UUID** |  | [optional] 
**repository_id** | **UUID** |  | [optional] 
**user_id** | **str** |  | [optional] 
**promotion_enabled** | **bool** |  | [optional] 
**commit_enabled** | **bool** |  | [optional] 
**checkpoint_enabled** | **bool** |  | [optional] 
**save_enabled** | **bool** |  | [optional] 
**tag_enabled** | **bool** |  | [optional] 
**latest_promotion** | **UUID** |  | [optional] 
**latest_commit** | **UUID** |  | [optional] 
**latest_checkpoint** | **UUID** |  | [optional] 
**latest_save** | **UUID** |  | [optional] 
**created_at** | **datetime** |  | [optional] 
**updated_at** | [**BranchDtoUpdatedAt**](BranchDtoUpdatedAt.md) |  | [optional] 
**deleted_at** | [**BranchDtoUpdatedAt**](BranchDtoUpdatedAt.md) |  | [optional] 

## Example

```python
from grace_generated_openapi_probe.models.branch_dto import BranchDto

# TODO update the JSON string below
json = "{}"
# create an instance of BranchDto from a JSON string
branch_dto_instance = BranchDto.from_json(json)
# print the JSON string representation of the object
print(BranchDto.to_json())

# convert the object into a dict
branch_dto_dict = branch_dto_instance.to_dict()
# create an instance of BranchDto from a dict
branch_dto_from_dict = BranchDto.from_dict(branch_dto_dict)
```
[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


