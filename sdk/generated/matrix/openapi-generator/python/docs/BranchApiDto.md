# BranchApiDto

Public branch DTO returned through branch endpoints.

## Properties

Name | Type | Description | Notes
------------ | ------------- | ------------- | -------------
**var_class** | **str** |  | 
**branch_id** | **UUID** |  | 
**branch_name** | **str** |  | 
**parent_branch_id** | **UUID** |  | 
**owner_id** | **UUID** |  | 
**organization_id** | **UUID** |  | 
**repository_id** | **UUID** |  | 
**based_on** | [**ReferenceApiDto**](ReferenceApiDto.md) |  | 
**user_id** | **str** |  | 
**assign_enabled** | **bool** |  | 
**promotion_enabled** | **bool** |  | 
**commit_enabled** | **bool** |  | 
**checkpoint_enabled** | **bool** |  | 
**save_enabled** | **bool** |  | 
**tag_enabled** | **bool** |  | 
**external_enabled** | **bool** |  | 
**auto_rebase_enabled** | **bool** |  | 
**promotion_mode** | **str** |  | 
**latest_reference** | [**ReferenceApiDto**](ReferenceApiDto.md) |  | 
**latest_promotion** | [**ReferenceApiDto**](ReferenceApiDto.md) |  | 
**latest_commit** | [**ReferenceApiDto**](ReferenceApiDto.md) |  | 
**latest_checkpoint** | [**ReferenceApiDto**](ReferenceApiDto.md) |  | 
**latest_save** | [**ReferenceApiDto**](ReferenceApiDto.md) |  | 
**should_recompute_latest_references** | **bool** |  | 
**created_at** | **datetime** |  | 
**updated_at** | **datetime** |  | [optional] 
**deleted_at** | **datetime** |  | [optional] 
**delete_reason** | **str** |  | 

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


