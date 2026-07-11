# GetUploadUriParameters


## Properties

Name | Type | Description | Notes
------------ | ------------- | ------------- | -------------
**correlation_id** | **str** | Body DTO correlation id copied into Grace command/event metadata after request parsing. This field is distinct from the X-Correlation-Id transport header. | [optional] 
**principal** | **str** | The entity on whose behalf the action is being performed. | [optional] 
**properties** | **Dict[str, str]** | Allow-listed event properties. UploadSessionIds is the only client-settable key. | [optional] 
**owner_id** | **str** |  | [optional] 
**owner_name** | **str** |  | [optional] 
**organization_id** | **str** |  | [optional] 
**organization_name** | **str** |  | [optional] 
**repository_id** | **str** |  | [optional] 
**repository_name** | **str** |  | [optional] 
**file_versions** | [**List[FileVersion]**](FileVersion.md) |  | [optional] 

## Example

```python
from grace_generated_openapi_probe.models.get_upload_uri_parameters import GetUploadUriParameters

# TODO update the JSON string below
json = "{}"
# create an instance of GetUploadUriParameters from a JSON string
get_upload_uri_parameters_instance = GetUploadUriParameters.from_json(json)
# print the JSON string representation of the object
print(GetUploadUriParameters.to_json())

# convert the object into a dict
get_upload_uri_parameters_dict = get_upload_uri_parameters_instance.to_dict()
# create an instance of GetUploadUriParameters from a dict
get_upload_uri_parameters_from_dict = GetUploadUriParameters.from_dict(get_upload_uri_parameters_dict)
```
[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


