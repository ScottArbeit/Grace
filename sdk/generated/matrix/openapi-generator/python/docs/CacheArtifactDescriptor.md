# CacheArtifactDescriptor


## Properties

Name | Type | Description | Notes
------------ | ------------- | ------------- | -------------
**repository_id** | **UUID** |  | 
**directory_version_id** | **UUID** |  | 
**kind** | **str** |  | 
**sha256** | **str** |  | 
**size** | **int** |  | 

## Example

```python
from grace_generated_openapi_probe.models.cache_artifact_descriptor import CacheArtifactDescriptor

# TODO update the JSON string below
json = "{}"
# create an instance of CacheArtifactDescriptor from a JSON string
cache_artifact_descriptor_instance = CacheArtifactDescriptor.from_json(json)
# print the JSON string representation of the object
print(CacheArtifactDescriptor.to_json())

# convert the object into a dict
cache_artifact_descriptor_dict = cache_artifact_descriptor_instance.to_dict()
# create an instance of CacheArtifactDescriptor from a dict
cache_artifact_descriptor_from_dict = CacheArtifactDescriptor.from_dict(cache_artifact_descriptor_dict)
```
[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


