# DirectoryVersionHashLookupResult

Complete current directory version returned by hash lookup endpoints.

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
**recursive_size** | **int** |  | 
**created_at** | **datetime** |  | 
**hashes_validated** | **bool** |  | 

## Example

```python
from grace_generated_openapi_probe.models.directory_version_hash_lookup_result import DirectoryVersionHashLookupResult

# TODO update the JSON string below
json = "{}"
# create an instance of DirectoryVersionHashLookupResult from a JSON string
directory_version_hash_lookup_result_instance = DirectoryVersionHashLookupResult.from_json(json)
# print the JSON string representation of the object
print(DirectoryVersionHashLookupResult.to_json())

# convert the object into a dict
directory_version_hash_lookup_result_dict = directory_version_hash_lookup_result_instance.to_dict()
# create an instance of DirectoryVersionHashLookupResult from a dict
directory_version_hash_lookup_result_from_dict = DirectoryVersionHashLookupResult.from_dict(directory_version_hash_lookup_result_dict)
```
[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


