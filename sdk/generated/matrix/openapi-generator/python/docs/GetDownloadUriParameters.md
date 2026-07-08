# GetDownloadUriParameters


## Properties

Name | Type | Description | Notes
------------ | ------------- | ------------- | -------------
**correlation_id** | **str** | Body DTO correlation id copied into Grace command/event metadata after request parsing. This field is distinct from the X-Correlation-Id transport header. | [optional] 
**principal** | **str** | The entity on whose behalf the action is being performed. | [optional] 
**owner_id** | **str** |  | [optional] 
**owner_name** | **str** |  | [optional] 
**organization_id** | **str** |  | [optional] 
**organization_name** | **str** |  | [optional] 
**repository_id** | **str** |  | [optional] 
**repository_name** | **str** |  | [optional] 
**reference_id** | **UUID** |  | 
**relative_path** | **str** |  | 
**sha256_hash** | **str** | Lowercase 64-character SHA-256 version hash persisted on version DTOs. | [optional] 
**blake3_hash** | **str** | Lowercase 64-character BLAKE3 version hash persisted on new version graph DTOs. | [optional] 

## Example

```python
from grace_generated_openapi_probe.models.get_download_uri_parameters import GetDownloadUriParameters

# TODO update the JSON string below
json = "{}"
# create an instance of GetDownloadUriParameters from a JSON string
get_download_uri_parameters_instance = GetDownloadUriParameters.from_json(json)
# print the JSON string representation of the object
print(GetDownloadUriParameters.to_json())

# convert the object into a dict
get_download_uri_parameters_dict = get_download_uri_parameters_instance.to_dict()
# create an instance of GetDownloadUriParameters from a dict
get_download_uri_parameters_from_dict = GetDownloadUriParameters.from_dict(get_download_uri_parameters_dict)
```
[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


