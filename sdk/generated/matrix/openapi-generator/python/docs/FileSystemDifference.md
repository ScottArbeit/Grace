# FileSystemDifference


## Properties

Name | Type | Description | Notes
------------ | ------------- | ------------- | -------------
**difference_type** | **object** |  | 
**file_system_entry_type** | **object** |  | 
**relative_path** | **object** |  | 

## Example

```python
from grace_generated_openapi_probe.models.file_system_difference import FileSystemDifference

# TODO update the JSON string below
json = "{}"
# create an instance of FileSystemDifference from a JSON string
file_system_difference_instance = FileSystemDifference.from_json(json)
# print the JSON string representation of the object
print(FileSystemDifference.to_json())

# convert the object into a dict
file_system_difference_dict = file_system_difference_instance.to_dict()
# create an instance of FileSystemDifference from a dict
file_system_difference_from_dict = FileSystemDifference.from_dict(file_system_difference_dict)
```
[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


