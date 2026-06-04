# FileDiff


## Properties

Name | Type | Description | Notes
------------ | ------------- | ------------- | -------------
**relative_path** | **object** |  | 
**file_sha1** | **object** |  | 
**created_at1** | **object** |  | 
**file_sha2** | **object** |  | 
**created_at2** | **object** |  | 
**is_binary** | **bool** |  | 
**inline_diff** | **List[List[object]]** |  | 
**side_by_side_old** | **List[List[object]]** |  | 
**side_by_side_new** | **List[List[object]]** |  | 

## Example

```python
from grace_generated_openapi_probe.models.file_diff import FileDiff

# TODO update the JSON string below
json = "{}"
# create an instance of FileDiff from a JSON string
file_diff_instance = FileDiff.from_json(json)
# print the JSON string representation of the object
print(FileDiff.to_json())

# convert the object into a dict
file_diff_dict = file_diff_instance.to_dict()
# create an instance of FileDiff from a dict
file_diff_from_dict = FileDiff.from_dict(file_diff_dict)
```
[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


