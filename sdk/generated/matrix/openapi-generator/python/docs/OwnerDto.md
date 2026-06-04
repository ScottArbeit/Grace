# OwnerDto

(automatically generated)

## Properties

Name | Type | Description | Notes
------------ | ------------- | ------------- | -------------
**var_class** | **str** |  | [optional] 
**owner_id** | **UUID** |  | [optional] 
**owner_name** | **str** |  | [optional] 
**owner_type** | [**OwnerType**](OwnerType.md) |  | [optional] 
**description** | **str** |  | [optional] 
**search_visibility** | [**SearchVisibility**](SearchVisibility.md) |  | [optional] 
**created_at** | **datetime** |  | [optional] 
**updated_at** | [**BranchDtoUpdatedAt**](BranchDtoUpdatedAt.md) |  | [optional] 
**deleted_at** | [**BranchDtoUpdatedAt**](BranchDtoUpdatedAt.md) |  | [optional] 
**delete_reason** | **str** |  | [optional] 

## Example

```python
from grace_generated_openapi_probe.models.owner_dto import OwnerDto

# TODO update the JSON string below
json = "{}"
# create an instance of OwnerDto from a JSON string
owner_dto_instance = OwnerDto.from_json(json)
# print the JSON string representation of the object
print(OwnerDto.to_json())

# convert the object into a dict
owner_dto_dict = owner_dto_instance.to_dict()
# create an instance of OwnerDto from a dict
owner_dto_from_dict = OwnerDto.from_dict(owner_dto_dict)
```
[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


