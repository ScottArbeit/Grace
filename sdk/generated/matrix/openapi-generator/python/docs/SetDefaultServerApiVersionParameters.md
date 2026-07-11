# SetDefaultServerApiVersionParameters

Parameters for the /repository/setDefaultServerApiVersion endpoint.

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
**default_server_api_version** | **str** |  | [optional] 

## Example

```python
from grace_generated_openapi_probe.models.set_default_server_api_version_parameters import SetDefaultServerApiVersionParameters

# TODO update the JSON string below
json = "{}"
# create an instance of SetDefaultServerApiVersionParameters from a JSON string
set_default_server_api_version_parameters_instance = SetDefaultServerApiVersionParameters.from_json(json)
# print the JSON string representation of the object
print(SetDefaultServerApiVersionParameters.to_json())

# convert the object into a dict
set_default_server_api_version_parameters_dict = set_default_server_api_version_parameters_instance.to_dict()
# create an instance of SetDefaultServerApiVersionParameters from a dict
set_default_server_api_version_parameters_from_dict = SetDefaultServerApiVersionParameters.from_dict(set_default_server_api_version_parameters_dict)
```
[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


