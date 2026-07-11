# RepositoryDto

(automatically generated)

## Properties

Name | Type | Description | Notes
------------ | ------------- | ------------- | -------------
**var_class** | **str** |  | [optional] 
**repository_id** | **UUID** |  | [optional] 
**owner_id** | **UUID** |  | [optional] 
**organization_id** | **UUID** |  | [optional] 
**repository_name** | **str** |  | [optional] 
**object_storage_provider** | [**ObjectStorageProvider**](ObjectStorageProvider.md) |  | [optional] 
**storage_pool_id** | **str** | StoragePool-wide CAS scope identifier. Public clients treat this as server-provided placement evidence and must not use it to select storage accounts, containers, buckets, prefixes, or write authorization directly. | [optional] 
**storage_account_name** | **str** |  | [optional] 
**storage_container_name** | **str** |  | [optional] 
**repository_visibility** | [**RepositoryVisibility**](RepositoryVisibility.md) |  | [optional] 
**repository_status** | [**RepositoryStatus**](RepositoryStatus.md) |  | [optional] 
**allows_large_files** | **bool** |  | [optional] 
**allow_external_contributions** | **bool** |  | [optional] 
**branches** | **List[str]** |  | [optional] 
**default_server_api_version** | **str** |  | [optional] 
**default_branch_name** | **str** |  | [optional] 
**save_days** | **float** |  | [optional] 
**checkpoint_days** | **float** |  | [optional] 

## Example

```python
from grace_generated_openapi_probe.models.repository_dto import RepositoryDto

# TODO update the JSON string below
json = "{}"
# create an instance of RepositoryDto from a JSON string
repository_dto_instance = RepositoryDto.from_json(json)
# print the JSON string representation of the object
print(RepositoryDto.to_json())

# convert the object into a dict
repository_dto_dict = repository_dto_instance.to_dict()
# create an instance of RepositoryDto from a dict
repository_dto_from_dict = RepositoryDto.from_dict(repository_dto_dict)
```
[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


