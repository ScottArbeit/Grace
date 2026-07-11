# ReferenceParameters


## Properties

Name | Type | Description | Notes
------------ | ------------- | ------------- | -------------
**correlation_id** | **str** | Body DTO correlation id copied into Grace command/event metadata after request parsing. This field is distinct from the X-Correlation-Id transport header. | [optional] 
**principal** | **str** | The entity on whose behalf the action is being performed. | [optional] 
**properties** | **Dict[str, str]** | Allow-listed event properties. UploadSessionIds is the only client-settable key. | [optional] 
**reference_id** | **UUID** |  | [optional] 
**reference_type** | [**ReferenceType**](ReferenceType.md) |  | [optional] 
**reference_text** | **str** |  | [optional] 
**repository_id** | **UUID** |  | [optional] 
**repository_name** | **str** |  | [optional] 

## Example

```python
from grace_generated_openapi_probe.models.reference_parameters import ReferenceParameters

# TODO update the JSON string below
json = "{}"
# create an instance of ReferenceParameters from a JSON string
reference_parameters_instance = ReferenceParameters.from_json(json)
# print the JSON string representation of the object
print(ReferenceParameters.to_json())

# convert the object into a dict
reference_parameters_dict = reference_parameters_instance.to_dict()
# create an instance of ReferenceParameters from a dict
reference_parameters_from_dict = ReferenceParameters.from_dict(reference_parameters_dict)
```
[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


