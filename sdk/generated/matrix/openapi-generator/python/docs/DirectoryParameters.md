# DirectoryParameters


## Properties

Name | Type | Description | Notes
------------ | ------------- | ------------- | -------------
**correlation_id** | **str** | Body DTO correlation id copied into Grace command/event metadata after request parsing. This field is distinct from the X-Correlation-Id transport header. | [optional] 
**principal** | **str** | The entity on whose behalf the action is being performed. | [optional] 
**properties** | **Dict[str, str]** | Allow-listed event properties. UploadSessionIds is the only client-settable key. | [optional] 
**owner_id** | **UUID** |  | [optional] 
**owner_name** | **str** |  | [optional] 
**organization_id** | **UUID** |  | [optional] 
**organization_name** | **str** |  | [optional] 
**repository_id** | **UUID** |  | [optional] 
**repository_name** | **str** |  | [optional] 
**directory_version_id** | **UUID** |  | [optional] 

## Example

```python
from grace_generated_openapi_probe.models.directory_parameters import DirectoryParameters

# TODO update the JSON string below
json = "{}"
# create an instance of DirectoryParameters from a JSON string
directory_parameters_instance = DirectoryParameters.from_json(json)
# print the JSON string representation of the object
print(DirectoryParameters.to_json())

# convert the object into a dict
directory_parameters_dict = directory_parameters_instance.to_dict()
# create an instance of DirectoryParameters from a dict
directory_parameters_from_dict = DirectoryParameters.from_dict(directory_parameters_dict)
```
[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


