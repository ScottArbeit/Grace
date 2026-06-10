# AnnotationSourceReference

Reference that contributed annotation source rows.

## Properties

Name | Type | Description | Notes
------------ | ------------- | ------------- | -------------
**source_reference_id** | **str** |  | 
**reference_id** | **UUID** |  | 
**reference_type** | [**ReferenceType**](ReferenceType.md) |  | 
**reference_text** | **str** |  | 
**directory_version_id** | **UUID** |  | 
**created_at** | **datetime** |  | [optional] 
**created_by** | **str** |  | [optional] 

## Example

```python
from grace_generated_openapi_probe.models.annotation_source_reference import AnnotationSourceReference

# TODO update the JSON string below
json = "{}"
# create an instance of AnnotationSourceReference from a JSON string
annotation_source_reference_instance = AnnotationSourceReference.from_json(json)
# print the JSON string representation of the object
print(AnnotationSourceReference.to_json())

# convert the object into a dict
annotation_source_reference_dict = annotation_source_reference_instance.to_dict()
# create an instance of AnnotationSourceReference from a dict
annotation_source_reference_from_dict = AnnotationSourceReference.from_dict(annotation_source_reference_dict)
```
[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


