# SynchronizedRepositoryStatusDto


## Properties

Name | Type | Description | Notes
------------ | ------------- | ------------- | -------------
**state** | **str** |  | 
**repository_id** | **UUID** |  | 
**root_configuration_version** | **UUID** |  | 
**is_caught_up** | **bool** |  | 
**rebaseline_required** | **bool** |  | 
**is_blocked** | **bool** |  | 
**pending_operation_count** | **int** |  | 
**oldest_pending_age_milliseconds** | **int** |  | 
**projection_lag_count** | **int** |  | 
**last_completed_at** | **datetime** |  | 

## Example

```python
from grace_generated_openapi_probe.models.synchronized_repository_status_dto import SynchronizedRepositoryStatusDto

# TODO update the JSON string below
json = "{}"
# create an instance of SynchronizedRepositoryStatusDto from a JSON string
synchronized_repository_status_dto_instance = SynchronizedRepositoryStatusDto.from_json(json)
# print the JSON string representation of the object
print(SynchronizedRepositoryStatusDto.to_json())

# convert the object into a dict
synchronized_repository_status_dto_dict = synchronized_repository_status_dto_instance.to_dict()
# create an instance of SynchronizedRepositoryStatusDto from a dict
synchronized_repository_status_dto_from_dict = SynchronizedRepositoryStatusDto.from_dict(synchronized_repository_status_dto_dict)
```
[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


