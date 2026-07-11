# GetBySha256HashParameters

Parameters for the /directory/getBySha256Hash endpoint.

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
**directory_version_id** | **UUID** |  | [optional] 
**sha256_hash** | **str** | Lowercase or uppercase 2- to 64-character SHA-256 version hash prefix. | [optional] 

## Example

```python
from grace_generated_openapi_probe.models.get_by_sha256_hash_parameters import GetBySha256HashParameters

# TODO update the JSON string below
json = "{}"
# create an instance of GetBySha256HashParameters from a JSON string
get_by_sha256_hash_parameters_instance = GetBySha256HashParameters.from_json(json)
# print the JSON string representation of the object
print(GetBySha256HashParameters.to_json())

# convert the object into a dict
get_by_sha256_hash_parameters_dict = get_by_sha256_hash_parameters_instance.to_dict()
# create an instance of GetBySha256HashParameters from a dict
get_by_sha256_hash_parameters_from_dict = GetBySha256HashParameters.from_dict(get_by_sha256_hash_parameters_dict)
```
[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


