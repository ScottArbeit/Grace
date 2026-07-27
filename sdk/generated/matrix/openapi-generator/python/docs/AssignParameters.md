# AssignParameters

Parameters for the /branch/assign endpoint.

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
**reference_id** | **UUID** |  | 
**directory_version_id** | **UUID** |  | [optional] 
**sha256_hash** | **str** | Empty value or lowercase or uppercase 2- to 64-character SHA-256 version hash prefix. | [optional] 
**blake3_hash** | **str** | Empty value or lowercase or uppercase 2- to 64-character BLAKE3 version hash prefix. | [optional] 
**message** | **str** |  | [optional] 

## Example

```python
from grace_generated_openapi_probe.models.assign_parameters import AssignParameters

# TODO update the JSON string below
json = "{}"
# create an instance of AssignParameters from a JSON string
assign_parameters_instance = AssignParameters.from_json(json)
# print the JSON string representation of the object
print(AssignParameters.to_json())

# convert the object into a dict
assign_parameters_dict = assign_parameters_instance.to_dict()
# create an instance of AssignParameters from a dict
assign_parameters_from_dict = AssignParameters.from_dict(assign_parameters_dict)
```
[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


