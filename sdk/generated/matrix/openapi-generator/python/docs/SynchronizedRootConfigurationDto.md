# SynchronizedRootConfigurationDto


## Properties

Name | Type | Description | Notes
------------ | ------------- | ------------- | -------------
**repository_id** | **UUID** |  | 
**version** | **UUID** |  | 
**roots** | **List[str]** |  | 
**created_at** | **datetime** |  | 
**created_by** | **str** |  | 
**previous_version** | **UUID** |  | 

## Example

```python
from grace_generated_openapi_probe.models.synchronized_root_configuration_dto import SynchronizedRootConfigurationDto

# TODO update the JSON string below
json = "{}"
# create an instance of SynchronizedRootConfigurationDto from a JSON string
synchronized_root_configuration_dto_instance = SynchronizedRootConfigurationDto.from_json(json)
# print the JSON string representation of the object
print(SynchronizedRootConfigurationDto.to_json())

# convert the object into a dict
synchronized_root_configuration_dto_dict = synchronized_root_configuration_dto_instance.to_dict()
# create an instance of SynchronizedRootConfigurationDto from a dict
synchronized_root_configuration_dto_from_dict = SynchronizedRootConfigurationDto.from_dict(synchronized_root_configuration_dto_dict)
```
[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


