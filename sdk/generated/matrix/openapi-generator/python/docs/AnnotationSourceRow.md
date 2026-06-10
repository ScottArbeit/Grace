# AnnotationSourceRow

Source row range for an annotation span.

## Properties

Name | Type | Description | Notes
------------ | ------------- | ------------- | -------------
**source_row_id** | **str** |  | 
**source_reference_id** | **str** |  | 
**path** | **str** |  | 
**line_range** | [**AnnotationLineRange**](AnnotationLineRange.md) |  | 

## Example

```python
from grace_generated_openapi_probe.models.annotation_source_row import AnnotationSourceRow

# TODO update the JSON string below
json = "{}"
# create an instance of AnnotationSourceRow from a JSON string
annotation_source_row_instance = AnnotationSourceRow.from_json(json)
# print the JSON string representation of the object
print(AnnotationSourceRow.to_json())

# convert the object into a dict
annotation_source_row_dict = annotation_source_row_instance.to_dict()
# create an instance of AnnotationSourceRow from a dict
annotation_source_row_from_dict = AnnotationSourceRow.from_dict(annotation_source_row_dict)
```
[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


