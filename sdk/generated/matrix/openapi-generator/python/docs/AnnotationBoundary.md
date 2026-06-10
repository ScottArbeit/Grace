# AnnotationBoundary

Boundary where annotation could not continue through history.

## Properties

Name | Type | Description | Notes
------------ | ------------- | ------------- | -------------
**boundary_id** | **str** |  | 
**line_range** | [**AnnotationLineRange**](AnnotationLineRange.md) |  | 
**source_row_ids** | **List[str]** |  | 

## Example

```python
from grace_generated_openapi_probe.models.annotation_boundary import AnnotationBoundary

# TODO update the JSON string below
json = "{}"
# create an instance of AnnotationBoundary from a JSON string
annotation_boundary_instance = AnnotationBoundary.from_json(json)
# print the JSON string representation of the object
print(AnnotationBoundary.to_json())

# convert the object into a dict
annotation_boundary_dict = annotation_boundary_instance.to_dict()
# create an instance of AnnotationBoundary from a dict
annotation_boundary_from_dict = AnnotationBoundary.from_dict(annotation_boundary_dict)
```
[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


