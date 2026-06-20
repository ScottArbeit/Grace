# GetContentBlockDownloadUriParameters


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
**content_block_address** | **str** | Lowercase 64-character BLAKE3-derived ContentBlock address. | [optional] 
**storage_pool_id** | **str** | StoragePool-wide CAS scope identifier. | [optional] 
**manifest** | [**FileManifest**](FileManifest.md) |  | [optional] 
**upload_session_id** | **UUID** |  | [optional] 
**authorized_scope** | **str** |  | [optional] 

## Example

```python
from grace_generated_openapi_probe.models.get_content_block_download_uri_parameters import GetContentBlockDownloadUriParameters

# TODO update the JSON string below
json = "{}"
# create an instance of GetContentBlockDownloadUriParameters from a JSON string
get_content_block_download_uri_parameters_instance = GetContentBlockDownloadUriParameters.from_json(json)
# print the JSON string representation of the object
print(GetContentBlockDownloadUriParameters.to_json())

# convert the object into a dict
get_content_block_download_uri_parameters_dict = get_content_block_download_uri_parameters_instance.to_dict()
# create an instance of GetContentBlockDownloadUriParameters from a dict
get_content_block_download_uri_parameters_from_dict = GetContentBlockDownloadUriParameters.from_dict(get_content_block_download_uri_parameters_dict)
```
[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


