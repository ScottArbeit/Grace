# AnnotationLine

One annotated target line.

## Properties

Name | Type | Description | Notes
------------ | ------------- | ------------- | -------------
**line_number** | **int** |  | 
**text** | **str** |  | 

## Example

```python
from grace_generated_openapi_probe.models.annotation_line import AnnotationLine

# TODO update the JSON string below
json = "{}"
# create an instance of AnnotationLine from a JSON string
annotation_line_instance = AnnotationLine.from_json(json)
# print the JSON string representation of the object
print(AnnotationLine.to_json())

# convert the object into a dict
annotation_line_dict = annotation_line_instance.to_dict()
# create an instance of AnnotationLine from a dict
annotation_line_from_dict = AnnotationLine.from_dict(annotation_line_dict)
```
[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


