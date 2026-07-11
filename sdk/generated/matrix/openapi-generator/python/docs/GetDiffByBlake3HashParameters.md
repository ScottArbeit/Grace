# GetDiffByBlake3HashParameters

Parameters for retrieving a diff by BLAKE3 hash or unique BLAKE3 prefix.

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
**blake3_hash1** | **str** | Lowercase or uppercase 2- to 64-character BLAKE3 version hash prefix used for version graph lookups. | [optional] 
**blake3_hash2** | **str** | Lowercase or uppercase 2- to 64-character BLAKE3 version hash prefix used for version graph lookups. | [optional] 

## Example

```python
from grace_generated_openapi_probe.models.get_diff_by_blake3_hash_parameters import GetDiffByBlake3HashParameters

# TODO update the JSON string below
json = "{}"
# create an instance of GetDiffByBlake3HashParameters from a JSON string
get_diff_by_blake3_hash_parameters_instance = GetDiffByBlake3HashParameters.from_json(json)
# print the JSON string representation of the object
print(GetDiffByBlake3HashParameters.to_json())

# convert the object into a dict
get_diff_by_blake3_hash_parameters_dict = get_diff_by_blake3_hash_parameters_instance.to_dict()
# create an instance of GetDiffByBlake3HashParameters from a dict
get_diff_by_blake3_hash_parameters_from_dict = GetDiffByBlake3HashParameters.from_dict(get_diff_by_blake3_hash_parameters_dict)
```
[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


