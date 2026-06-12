# CreateReferenceParameters

Parameters for /branch/promote, /branch/commit, /branch/checkpoint, /branch/save, and /branch/tag.

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
**directory_version_id** | **UUID** |  | [optional] 
**sha256_hash** | **str** | Empty value or lowercase or uppercase 2- to 64-character SHA-256 version hash prefix. | [optional] 
**blake3_hash** | **str** | Empty value or lowercase or uppercase 2- to 64-character BLAKE3 version hash prefix. | [optional] 
**message** | **str** |  | [optional] 

## Example

```python
from grace_generated_openapi_probe.models.create_reference_parameters import CreateReferenceParameters

# TODO update the JSON string below
json = "{}"
# create an instance of CreateReferenceParameters from a JSON string
create_reference_parameters_instance = CreateReferenceParameters.from_json(json)
# print the JSON string representation of the object
print(CreateReferenceParameters.to_json())

# convert the object into a dict
create_reference_parameters_dict = create_reference_parameters_instance.to_dict()
# create an instance of CreateReferenceParameters from a dict
create_reference_parameters_from_dict = CreateReferenceParameters.from_dict(create_reference_parameters_dict)
```
[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


