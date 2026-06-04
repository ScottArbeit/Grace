# GetReferencesParameters

Parameters for /branch/getReferences and reference-type list endpoints.

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
**branch_id** | **UUID** |  | [optional] 
**branch_name** | **str** |  | [optional] 
**full_sha** | **bool** |  | [optional] 
**max_count** | **int** |  | [optional] 

## Example

```python
from grace_generated_openapi_probe.models.get_references_parameters import GetReferencesParameters

# TODO update the JSON string below
json = "{}"
# create an instance of GetReferencesParameters from a JSON string
get_references_parameters_instance = GetReferencesParameters.from_json(json)
# print the JSON string representation of the object
print(GetReferencesParameters.to_json())

# convert the object into a dict
get_references_parameters_dict = get_references_parameters_instance.to_dict()
# create an instance of GetReferencesParameters from a dict
get_references_parameters_from_dict = GetReferencesParameters.from_dict(get_references_parameters_dict)
```
[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


