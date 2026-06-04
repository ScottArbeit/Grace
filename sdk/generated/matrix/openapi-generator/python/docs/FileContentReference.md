# FileContentReference

Identifies whether a FileVersion refers to whole-file bytes or a FileManifest.

## Properties

Name | Type | Description | Notes
------------ | ------------- | ------------- | -------------
**var_class** | **str** |  | 
**reference_type** | **str** |  | 
**manifest** | [**FileManifest**](FileManifest.md) |  | 

## Example

```python
from grace_generated_openapi_probe.models.file_content_reference import FileContentReference

# TODO update the JSON string below
json = "{}"
# create an instance of FileContentReference from a JSON string
file_content_reference_instance = FileContentReference.from_json(json)
# print the JSON string representation of the object
print(FileContentReference.to_json())

# convert the object into a dict
file_content_reference_dict = file_content_reference_instance.to_dict()
# create an instance of FileContentReference from a dict
file_content_reference_from_dict = FileContentReference.from_dict(file_content_reference_dict)
```
[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


