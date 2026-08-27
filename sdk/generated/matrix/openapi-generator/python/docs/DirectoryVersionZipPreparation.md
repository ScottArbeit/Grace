# DirectoryVersionZipPreparation


## Properties

Name | Type | Description | Notes
------------ | ------------- | ------------- | -------------
**artifact** | [**DirectoryVersionZipCacheArtifact**](DirectoryVersionZipCacheArtifact.md) |  | 
**artifact_grant** | **str** |  | 
**artifact_grant_expires_at** | **datetime** |  | 
**permit** | **str** |  | 
**permit_expires_at** | **datetime** |  | 
**redemption_bytes** | **str** |  | 

## Example

```python
from grace_generated_openapi_probe.models.directory_version_zip_preparation import DirectoryVersionZipPreparation

# TODO update the JSON string below
json = "{}"
# create an instance of DirectoryVersionZipPreparation from a JSON string
directory_version_zip_preparation_instance = DirectoryVersionZipPreparation.from_json(json)
# print the JSON string representation of the object
print(DirectoryVersionZipPreparation.to_json())

# convert the object into a dict
directory_version_zip_preparation_dict = directory_version_zip_preparation_instance.to_dict()
# create an instance of DirectoryVersionZipPreparation from a dict
directory_version_zip_preparation_from_dict = DirectoryVersionZipPreparation.from_dict(directory_version_zip_preparation_dict)
```
[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


