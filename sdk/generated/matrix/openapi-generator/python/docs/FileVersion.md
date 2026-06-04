# FileVersion


## Properties

Name | Type | Description | Notes
------------ | ------------- | ------------- | -------------
**var_class** | **str** |  | [optional] 
**is_binary** | **bool** |  | [optional] 
**size** | **int** |  | [optional] 
**created_at** | **datetime** |  | [optional] 
**blob_uri** | **str** |  | [optional] 

## Example

```python
from grace_generated_openapi_probe.models.file_version import FileVersion

# TODO update the JSON string below
json = "{}"
# create an instance of FileVersion from a JSON string
file_version_instance = FileVersion.from_json(json)
# print the JSON string representation of the object
print(FileVersion.to_json())

# convert the object into a dict
file_version_dict = file_version_instance.to_dict()
# create an instance of FileVersion from a dict
file_version_from_dict = FileVersion.from_dict(file_version_dict)
```
[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


