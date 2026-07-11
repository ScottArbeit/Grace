# AnnotateParameters

Parameters for the /branch/annotate endpoint.

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
**branch_id** | **UUID** |  | [optional] 
**branch_name** | **str** |  | [optional] 
**target_reference_id** | **UUID** |  | [optional] 
**path** | **str** |  | [optional] 
**start_line** | **int** |  | [optional] 
**end_line** | **int** |  | [optional] 
**reference_types** | [**List[ReferenceType]**](ReferenceType.md) |  | [optional] 
**max_references** | **int** |  | [optional] 
**include_line_text** | **bool** |  | [optional] 

## Example

```python
from grace_generated_openapi_probe.models.annotate_parameters import AnnotateParameters

# TODO update the JSON string below
json = "{}"
# create an instance of AnnotateParameters from a JSON string
annotate_parameters_instance = AnnotateParameters.from_json(json)
# print the JSON string representation of the object
print(AnnotateParameters.to_json())

# convert the object into a dict
annotate_parameters_dict = annotate_parameters_instance.to_dict()
# create an instance of AnnotateParameters from a dict
annotate_parameters_from_dict = AnnotateParameters.from_dict(annotate_parameters_dict)
```
[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


