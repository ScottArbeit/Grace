# DirectoryVersionZipFillSource


## Properties

Name | Type | Description | Notes
------------ | ------------- | ------------- | -------------
**descriptor** | [**CacheArtifactDescriptor**](CacheArtifactDescriptor.md) |  | 
**source_uri** | **str** |  | 
**source_expires_at** | **datetime** |  | 

## Example

```python
from grace_generated_openapi_probe.models.directory_version_zip_fill_source import DirectoryVersionZipFillSource

# TODO update the JSON string below
json = "{}"
# create an instance of DirectoryVersionZipFillSource from a JSON string
directory_version_zip_fill_source_instance = DirectoryVersionZipFillSource.from_json(json)
# print the JSON string representation of the object
print(DirectoryVersionZipFillSource.to_json())

# convert the object into a dict
directory_version_zip_fill_source_dict = directory_version_zip_fill_source_instance.to_dict()
# create an instance of DirectoryVersionZipFillSource from a dict
directory_version_zip_fill_source_from_dict = DirectoryVersionZipFillSource.from_dict(directory_version_zip_fill_source_dict)
```
[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


