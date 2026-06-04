# BranchApiDto

Public branch DTO returned through branch endpoints.

## Properties

Name | Type | Description | Notes
------------ | ------------- | ------------- | -------------
**var_class** | **str** |  | [optional] 
**branch_id** | **UUID** |  | [optional] 
**branch_name** | **str** |  | [optional] 
**parent_branch_id** | **UUID** |  | [optional] 
**owner_id** | **UUID** |  | [optional] 
**organization_id** | **UUID** |  | [optional] 
**repository_id** | **UUID** |  | [optional] 
**based_on** | [**ReferenceApiDto**](ReferenceApiDto.md) |  | [optional] 
**user_id** | **str** |  | [optional] 
**assign_enabled** | **bool** |  | [optional] 
**promotion_enabled** | **bool** |  | [optional] 
**commit_enabled** | **bool** |  | [optional] 
**checkpoint_enabled** | **bool** |  | [optional] 
**save_enabled** | **bool** |  | [optional] 
**tag_enabled** | **bool** |  | [optional] 
**external_enabled** | **bool** |  | [optional] 
**auto_rebase_enabled** | **bool** |  | [optional] 
**promotion_mode** | **str** |  | [optional] 
**latest_reference** | [**ReferenceApiDto**](ReferenceApiDto.md) |  | [optional] 
**latest_promotion** | [**ReferenceApiDto**](ReferenceApiDto.md) |  | [optional] 
**latest_commit** | [**ReferenceApiDto**](ReferenceApiDto.md) |  | [optional] 
**latest_checkpoint** | [**ReferenceApiDto**](ReferenceApiDto.md) |  | [optional] 
**latest_save** | [**ReferenceApiDto**](ReferenceApiDto.md) |  | [optional] 
**should_recompute_latest_references** | **bool** |  | [optional] 
**created_at** | **datetime** |  | [optional] 
**updated_at** | **datetime** |  | [optional] 
**deleted_at** | **datetime** |  | [optional] 
**delete_reason** | **str** |  | [optional] 

## Example

```python
from grace_generated_openapi_probe.models.branch_api_dto import BranchApiDto

# TODO update the JSON string below
json = "{}"
# create an instance of BranchApiDto from a JSON string
branch_api_dto_instance = BranchApiDto.from_json(json)
# print the JSON string representation of the object
print(BranchApiDto.to_json())

# convert the object into a dict
branch_api_dto_dict = branch_api_dto_instance.to_dict()
# create an instance of BranchApiDto from a dict
branch_api_dto_from_dict = BranchApiDto.from_dict(branch_api_dto_dict)
```
[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


