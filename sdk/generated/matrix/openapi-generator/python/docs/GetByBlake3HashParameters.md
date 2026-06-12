# GetByBlake3HashParameters

Parameters for the /directory/getByBlake3Hash endpoint.

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
**directory_version_id** | **UUID** |  | [optional] 
**blake3_hash** | **str** | Lowercase or uppercase 64-character BLAKE3 version hash used for new version graph lookups. | [optional] 

## Example

```python
from grace_generated_openapi_probe.models.get_by_blake3_hash_parameters import GetByBlake3HashParameters

# TODO update the JSON string below
json = "{}"
# create an instance of GetByBlake3HashParameters from a JSON string
get_by_blake3_hash_parameters_instance = GetByBlake3HashParameters.from_json(json)
# print the JSON string representation of the object
print(GetByBlake3HashParameters.to_json())

# convert the object into a dict
get_by_blake3_hash_parameters_dict = get_by_blake3_hash_parameters_instance.to_dict()
# create an instance of GetByBlake3HashParameters from a dict
get_by_blake3_hash_parameters_from_dict = GetByBlake3HashParameters.from_dict(get_by_blake3_hash_parameters_dict)
```
[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


