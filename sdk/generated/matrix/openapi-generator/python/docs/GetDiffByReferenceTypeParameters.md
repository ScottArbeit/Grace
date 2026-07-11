# GetDiffByReferenceTypeParameters

Parameters for retrieving a diff by reference type.

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
**directory_version_id1** | **UUID** |  | [optional] 
**directory_version_id2** | **UUID** |  | [optional] 
**branch_id** | **UUID** |  | [optional] 
**branch_name** | **str** |  | [optional] 

## Example

```python
from grace_generated_openapi_probe.models.get_diff_by_reference_type_parameters import GetDiffByReferenceTypeParameters

# TODO update the JSON string below
json = "{}"
# create an instance of GetDiffByReferenceTypeParameters from a JSON string
get_diff_by_reference_type_parameters_instance = GetDiffByReferenceTypeParameters.from_json(json)
# print the JSON string representation of the object
print(GetDiffByReferenceTypeParameters.to_json())

# convert the object into a dict
get_diff_by_reference_type_parameters_dict = get_diff_by_reference_type_parameters_instance.to_dict()
# create an instance of GetDiffByReferenceTypeParameters from a dict
get_diff_by_reference_type_parameters_from_dict = GetDiffByReferenceTypeParameters.from_dict(get_diff_by_reference_type_parameters_dict)
```
[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


