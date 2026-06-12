# DirectoryVersion


## Properties

Name | Type | Description | Notes
------------ | ------------- | ------------- | -------------
**var_class** | **str** |  | [optional] 
**directory_version_id** | **UUID** |  | [optional] 
**owner_id** | **UUID** |  | [optional] 
**organization_id** | **UUID** |  | [optional] 
**repository_id** | **UUID** |  | [optional] 
**relative_path** | **str** |  | [optional] 
**sha256_hash** | **str** | Lowercase or uppercase 64-character SHA-256 version hash retained for compatibility. | [optional] 
**blake3_hash** | **str** | Empty or 64-character BLAKE3 version hash for legacy version DTOs. | [optional] 
**directories** | **List[UUID]** |  | [optional] 
**files** | [**List[FileVersion]**](FileVersion.md) |  | [optional] 
**size** | **int** |  | [optional] 
**recursive_size** | **int** |  | [optional] 
**created_at** | **datetime** |  | [optional] 
**hashes_validated** | **bool** |  | [optional] 

## Example

```python
from grace_generated_openapi_probe.models.directory_version import DirectoryVersion

# TODO update the JSON string below
json = "{}"
# create an instance of DirectoryVersion from a JSON string
directory_version_instance = DirectoryVersion.from_json(json)
# print the JSON string representation of the object
print(DirectoryVersion.to_json())

# convert the object into a dict
directory_version_dict = directory_version_instance.to_dict()
# create an instance of DirectoryVersion from a dict
directory_version_from_dict = DirectoryVersion.from_dict(directory_version_dict)
```
[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


