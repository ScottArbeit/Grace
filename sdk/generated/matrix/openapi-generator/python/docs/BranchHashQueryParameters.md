# BranchHashQueryParameters

Parameters for branch query endpoints that support SHA-256, BLAKE3, or reference id locators.

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
from grace_generated_openapi_probe.models.branch_hash_query_parameters import BranchHashQueryParameters

# TODO update the JSON string below
json = "{}"
# create an instance of BranchHashQueryParameters from a JSON string
branch_hash_query_parameters_instance = BranchHashQueryParameters.from_json(json)
# print the JSON string representation of the object
print(BranchHashQueryParameters.to_json())

# convert the object into a dict
branch_hash_query_parameters_dict = branch_hash_query_parameters_instance.to_dict()
# create an instance of BranchHashQueryParameters from a dict
branch_hash_query_parameters_from_dict = BranchHashQueryParameters.from_dict(branch_hash_query_parameters_dict)
```
[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


