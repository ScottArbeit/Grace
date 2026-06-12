# SwitchParameters

Parameters for the /branch/switch endpoint.

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
**sha256_hash** | **str** | Lowercase or uppercase 64-character SHA-256 version hash retained for compatibility. | [optional] 
**reference_id** | **UUID** |  | [optional] 
**blake3_hash** | **str** | Lowercase or uppercase 64-character BLAKE3 version hash used for new version graph lookups. | [optional] 

## Example

```python
from grace_generated_openapi_probe.models.switch_parameters import SwitchParameters

# TODO update the JSON string below
json = "{}"
# create an instance of SwitchParameters from a JSON string
switch_parameters_instance = SwitchParameters.from_json(json)
# print the JSON string representation of the object
print(SwitchParameters.to_json())

# convert the object into a dict
switch_parameters_dict = switch_parameters_instance.to_dict()
# create an instance of SwitchParameters from a dict
switch_parameters_from_dict = SwitchParameters.from_dict(switch_parameters_dict)
```
[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


