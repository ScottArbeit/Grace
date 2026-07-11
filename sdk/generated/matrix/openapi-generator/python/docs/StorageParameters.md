# StorageParameters


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

## Example

```python
from grace_generated_openapi_probe.models.storage_parameters import StorageParameters

# TODO update the JSON string below
json = "{}"
# create an instance of StorageParameters from a JSON string
storage_parameters_instance = StorageParameters.from_json(json)
# print the JSON string representation of the object
print(StorageParameters.to_json())

# convert the object into a dict
storage_parameters_dict = storage_parameters_instance.to_dict()
# create an instance of StorageParameters from a dict
storage_parameters_from_dict = StorageParameters.from_dict(storage_parameters_dict)
```
[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


