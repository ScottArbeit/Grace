# AnnotationLineRange

Inclusive one-based line range.

## Properties

Name | Type | Description | Notes
------------ | ------------- | ------------- | -------------
**start_line** | **int** |  | 
**end_line** | **int** |  | 

## Example

```python
from grace_generated_openapi_probe.models.annotation_line_range import AnnotationLineRange

# TODO update the JSON string below
json = "{}"
# create an instance of AnnotationLineRange from a JSON string
annotation_line_range_instance = AnnotationLineRange.from_json(json)
# print the JSON string representation of the object
print(AnnotationLineRange.to_json())

# convert the object into a dict
annotation_line_range_dict = annotation_line_range_instance.to_dict()
# create an instance of AnnotationLineRange from a dict
annotation_line_range_from_dict = AnnotationLineRange.from_dict(annotation_line_range_dict)
```
[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


