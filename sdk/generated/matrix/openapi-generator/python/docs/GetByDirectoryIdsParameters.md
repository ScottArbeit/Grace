# GetByDirectoryIdsParameters

Parameters for the /directory/getByDirectoryIds endpoint.

## Properties

Name | Type | Description | Notes
------------ | ------------- | ------------- | -------------
**correlation_id** | **str** | Body DTO correlation id copied into Grace command/event metadata after request parsing. This field is distinct from the X-Correlation-Id transport header. | [optional] 
**principal** | **str** | The entity on whose behalf the action is being performed. | [optional] 
**owner_id** | **UUID** |  | [optional] 
**owner_name** | **str** |  | [optional] 
**organization_id** | **UUID** |  | [optional] 
**organization_name** | **str** |  | [optional] 
**repository_id** | **UUID** |  | [optional] 
**repository_name** | **str** |  | [optional] 
**directory_version_id** | **UUID** |  | [optional] 
**directory_ids** | **List[UUID]** |  | [optional] 

## Example

```python
from grace_generated_openapi_probe.models.get_by_directory_ids_parameters import GetByDirectoryIdsParameters

# TODO update the JSON string below
json = "{}"
# create an instance of GetByDirectoryIdsParameters from a JSON string
get_by_directory_ids_parameters_instance = GetByDirectoryIdsParameters.from_json(json)
# print the JSON string representation of the object
print(GetByDirectoryIdsParameters.to_json())

# convert the object into a dict
get_by_directory_ids_parameters_dict = get_by_directory_ids_parameters_instance.to_dict()
# create an instance of GetByDirectoryIdsParameters from a dict
get_by_directory_ids_parameters_from_dict = GetByDirectoryIdsParameters.from_dict(get_by_directory_ids_parameters_dict)
```
[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


