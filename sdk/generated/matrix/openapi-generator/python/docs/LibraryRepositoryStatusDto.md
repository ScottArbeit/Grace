# LibraryRepositoryStatusDto


## Properties

Name | Type | Description | Notes
------------ | ------------- | ------------- | -------------
**state** | **str** |  | 
**repository_id** | **UUID** |  | 
**library_catalog_version** | **UUID** |  | 
**is_caught_up** | **bool** |  | 
**rebaseline_required** | **bool** |  | 
**is_blocked** | **bool** |  | 
**pending_operation_count** | **int** |  | 
**oldest_pending_age_milliseconds** | **int** |  | 
**projection_lag_count** | **int** |  | 
**last_completed_at** | **datetime** |  | 

## Example

```python
from grace_generated_openapi_probe.models.library_repository_status_dto import LibraryRepositoryStatusDto

# TODO update the JSON string below
json = "{}"
# create an instance of LibraryRepositoryStatusDto from a JSON string
library_repository_status_dto_instance = LibraryRepositoryStatusDto.from_json(json)
# print the JSON string representation of the object
print(LibraryRepositoryStatusDto.to_json())

# convert the object into a dict
library_repository_status_dto_dict = library_repository_status_dto_instance.to_dict()
# create an instance of LibraryRepositoryStatusDto from a dict
library_repository_status_dto_from_dict = LibraryRepositoryStatusDto.from_dict(library_repository_status_dto_dict)
```
[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


