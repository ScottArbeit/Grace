# RegisterContentBlockUploadParameters


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
**upload_session_id** | **UUID** |  | [optional] 
**authorized_scope** | **str** |  | [optional] 
**operation_id** | **str** | Caller-supplied idempotency key for one upload-session operation. | [optional] 
**content_block_address** | **str** | Lowercase 64-character BLAKE3-derived ContentBlock address. | [optional] 
**logical_offset** | **int** |  | [optional] 
**logical_length** | **int** |  | [optional] 
**expected_payload_length** | **int** |  | [optional] 

## Example

```python
from grace_generated_openapi_probe.models.register_content_block_upload_parameters import RegisterContentBlockUploadParameters

# TODO update the JSON string below
json = "{}"
# create an instance of RegisterContentBlockUploadParameters from a JSON string
register_content_block_upload_parameters_instance = RegisterContentBlockUploadParameters.from_json(json)
# print the JSON string representation of the object
print(RegisterContentBlockUploadParameters.to_json())

# convert the object into a dict
register_content_block_upload_parameters_dict = register_content_block_upload_parameters_instance.to_dict()
# create an instance of RegisterContentBlockUploadParameters from a dict
register_content_block_upload_parameters_from_dict = RegisterContentBlockUploadParameters.from_dict(register_content_block_upload_parameters_dict)
```
[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


