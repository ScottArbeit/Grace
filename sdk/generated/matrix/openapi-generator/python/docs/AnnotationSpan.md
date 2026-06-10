# AnnotationSpan

Annotated target span and its source rows.

## Properties

Name | Type | Description | Notes
------------ | ------------- | ------------- | -------------
**span_id** | **str** |  | 
**boundary_id** | **str** |  | 
**line_range** | [**AnnotationLineRange**](AnnotationLineRange.md) |  | 
**source_row_ids** | **List[str]** |  | 

## Example

```python
from grace_generated_openapi_probe.models.annotation_span import AnnotationSpan

# TODO update the JSON string below
json = "{}"
# create an instance of AnnotationSpan from a JSON string
annotation_span_instance = AnnotationSpan.from_json(json)
# print the JSON string representation of the object
print(AnnotationSpan.to_json())

# convert the object into a dict
annotation_span_dict = annotation_span_instance.to_dict()
# create an instance of AnnotationSpan from a dict
annotation_span_from_dict = AnnotationSpan.from_dict(annotation_span_dict)
```
[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


