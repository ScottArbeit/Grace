# PrepareSynchronizedContentParameters


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
**operation_id** | **UUID** |  | 
**blake3_hash** | **str** | Lowercase 64-character BLAKE3 version hash persisted on new version graph DTOs. | 
**sha256_hash** | **str** | Lowercase 64-character SHA-256 version hash persisted on version DTOs. | 
**size** | **int** |  | 

## Example

```python
from grace_generated_openapi_probe.models.prepare_synchronized_content_parameters import PrepareSynchronizedContentParameters

# TODO update the JSON string below
json = "{}"
# create an instance of PrepareSynchronizedContentParameters from a JSON string
prepare_synchronized_content_parameters_instance = PrepareSynchronizedContentParameters.from_json(json)
# print the JSON string representation of the object
print(PrepareSynchronizedContentParameters.to_json())

# convert the object into a dict
prepare_synchronized_content_parameters_dict = prepare_synchronized_content_parameters_instance.to_dict()
# create an instance of PrepareSynchronizedContentParameters from a dict
prepare_synchronized_content_parameters_from_dict = PrepareSynchronizedContentParameters.from_dict(prepare_synchronized_content_parameters_dict)
```
[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


