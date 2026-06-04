# UploadSessionStorageParameters


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

## Example

```python
from grace_generated_openapi_probe.models.upload_session_storage_parameters import UploadSessionStorageParameters

# TODO update the JSON string below
json = "{}"
# create an instance of UploadSessionStorageParameters from a JSON string
upload_session_storage_parameters_instance = UploadSessionStorageParameters.from_json(json)
# print the JSON string representation of the object
print(UploadSessionStorageParameters.to_json())

# convert the object into a dict
upload_session_storage_parameters_dict = upload_session_storage_parameters_instance.to_dict()
# create an instance of UploadSessionStorageParameters from a dict
upload_session_storage_parameters_from_dict = UploadSessionStorageParameters.from_dict(upload_session_storage_parameters_dict)
```
[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


