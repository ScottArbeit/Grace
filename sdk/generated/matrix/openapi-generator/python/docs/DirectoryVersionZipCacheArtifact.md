# DirectoryVersionZipCacheArtifact


## Properties

Name | Type | Description | Notes
------------ | ------------- | ------------- | -------------
**repository_id** | **UUID** |  | 
**directory_version_id** | **UUID** |  | 
**blake3_hash** | **str** |  | 

## Example

```python
from grace_generated_openapi_probe.models.directory_version_zip_cache_artifact import DirectoryVersionZipCacheArtifact

# TODO update the JSON string below
json = "{}"
# create an instance of DirectoryVersionZipCacheArtifact from a JSON string
directory_version_zip_cache_artifact_instance = DirectoryVersionZipCacheArtifact.from_json(json)
# print the JSON string representation of the object
print(DirectoryVersionZipCacheArtifact.to_json())

# convert the object into a dict
directory_version_zip_cache_artifact_dict = directory_version_zip_cache_artifact_instance.to_dict()
# create an instance of DirectoryVersionZipCacheArtifact from a dict
directory_version_zip_cache_artifact_from_dict = DirectoryVersionZipCacheArtifact.from_dict(directory_version_zip_cache_artifact_dict)
```
[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


