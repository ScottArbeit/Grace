# DirectoryVersion


## Properties

Name | Type | Description | Notes
------------ | ------------- | ------------- | -------------
**var_class** | **str** |  | 
**directory_version_id** | **UUID** |  | 
**owner_id** | **UUID** |  | 
**organization_id** | **UUID** |  | 
**repository_id** | **UUID** |  | 
**relative_path** | **str** |  | 
**sha256_hash** | **str** | Lowercase 64-character SHA-256 version hash persisted on version DTOs. | 
**blake3_hash** | **str** | Lowercase 64-character BLAKE3 version hash persisted on new version graph DTOs. | 
**directories** | **List[UUID]** |  | 
**files** | [**List[FileVersion]**](FileVersion.md) |  | 
**size** | **int** |  | 
**recursive_size** | **int** |  | [optional] 
**created_at** | **datetime** |  | 
**hashes_validated** | **bool** |  | 

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


