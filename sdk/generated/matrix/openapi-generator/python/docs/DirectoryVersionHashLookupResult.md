# DirectoryVersionHashLookupResult

Raw directory version returned by hash lookup endpoints. Sha256Hash may be empty only when /directory/getBySha256Hash deliberately returns DirectoryVersion.Default as its no-match sentinel.

## Properties

Name | Type | Description | Notes
------------ | ------------- | ------------- | -------------
**var_class** | **str** |  | [optional] 
**directory_version_id** | **UUID** |  | [optional] 
**owner_id** | **UUID** |  | [optional] 
**organization_id** | **UUID** |  | [optional] 
**repository_id** | **UUID** |  | [optional] 
**relative_path** | **str** |  | [optional] 
**sha256_hash** | **str** | Empty value only for the DirectoryVersion.Default no-match sentinel, or lowercase 64-character SHA-256 hash. | [optional] 
**blake3_hash** | **str** | Lowercase 64-character BLAKE3 version hash persisted on new version graph DTOs. | [optional] 
**directories** | **List[UUID]** |  | [optional] 
**files** | [**List[FileVersion]**](FileVersion.md) |  | [optional] 
**size** | **int** |  | [optional] 
**recursive_size** | **int** |  | [optional] 
**created_at** | **datetime** |  | [optional] 
**hashes_validated** | **bool** |  | [optional] 

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


