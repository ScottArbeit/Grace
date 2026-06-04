# OrganizationDto

(automatically generated)

## Properties

Name | Type | Description | Notes
------------ | ------------- | ------------- | -------------
**var_class** | **str** |  | [optional] 
**organization_id** | **UUID** |  | [optional] 
**organization_name** | **str** |  | [optional] 
**owner_id** | **UUID** |  | [optional] 
**organization_type** | [**OrganizationType**](OrganizationType.md) |  | [optional] 
**description** | **str** |  | [optional] 
**search_visibility** | [**SearchVisibility**](SearchVisibility.md) |  | [optional] 
**created_at** | **datetime** |  | [optional] 
**updated_at** | [**BranchDtoUpdatedAt**](BranchDtoUpdatedAt.md) |  | [optional] 
**deleted_at** | [**BranchDtoUpdatedAt**](BranchDtoUpdatedAt.md) |  | [optional] 
**delete_reason** | **str** |  | [optional] 

## Example

```python
from grace_generated_openapi_probe.models.organization_dto import OrganizationDto

# TODO update the JSON string below
json = "{}"
# create an instance of OrganizationDto from a JSON string
organization_dto_instance = OrganizationDto.from_json(json)
# print the JSON string representation of the object
print(OrganizationDto.to_json())

# convert the object into a dict
organization_dto_dict = organization_dto_instance.to_dict()
# create an instance of OrganizationDto from a dict
organization_dto_from_dict = OrganizationDto.from_dict(organization_dto_dict)
```
[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


