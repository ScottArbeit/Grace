# GetContentBlockUploadUriParameters


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
**authorized_scope** | **str** |  | [optional] 

## Example

```python
from grace_generated_openapi_probe.models.get_content_block_upload_uri_parameters import GetContentBlockUploadUriParameters

# TODO update the JSON string below
json = "{}"
# create an instance of GetContentBlockUploadUriParameters from a JSON string
get_content_block_upload_uri_parameters_instance = GetContentBlockUploadUriParameters.from_json(json)
# print the JSON string representation of the object
print(GetContentBlockUploadUriParameters.to_json())

# convert the object into a dict
get_content_block_upload_uri_parameters_dict = get_content_block_upload_uri_parameters_instance.to_dict()
# create an instance of GetContentBlockUploadUriParameters from a dict
get_content_block_upload_uri_parameters_from_dict = GetContentBlockUploadUriParameters.from_dict(get_content_block_upload_uri_parameters_dict)
```
[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


