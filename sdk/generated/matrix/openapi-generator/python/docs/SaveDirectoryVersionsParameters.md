# SaveDirectoryVersionsParameters

Parameters for the /directory/saveDirectoryVersions endpoint.

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
**directory_versions** | [**List[DirectoryVersion]**](DirectoryVersion.md) |  | [optional] 

## Example

```python
from grace_generated_openapi_probe.models.save_directory_versions_parameters import SaveDirectoryVersionsParameters

# TODO update the JSON string below
json = "{}"
# create an instance of SaveDirectoryVersionsParameters from a JSON string
save_directory_versions_parameters_instance = SaveDirectoryVersionsParameters.from_json(json)
# print the JSON string representation of the object
print(SaveDirectoryVersionsParameters.to_json())

# convert the object into a dict
save_directory_versions_parameters_dict = save_directory_versions_parameters_instance.to_dict()
# create an instance of SaveDirectoryVersionsParameters from a dict
save_directory_versions_parameters_from_dict = SaveDirectoryVersionsParameters.from_dict(save_directory_versions_parameters_dict)
```
[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


