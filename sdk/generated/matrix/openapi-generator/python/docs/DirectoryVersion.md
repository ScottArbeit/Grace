# DirectoryVersion


## Properties

Name | Type | Description | Notes
------------ | ------------- | ------------- | -------------
**var_class** | **str** |  | [optional] 
**directories** | **List[object]** |  | [optional] 
**files** | **List[object]** |  | [optional] 
**size** | **int** |  | [optional] 
**recursive_size** | **int** |  | [optional] 
**created_at** | **datetime** |  | [optional] 

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


