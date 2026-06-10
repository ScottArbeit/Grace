# BranchAnnotationApiDto

Branch annotation result for an existing server-known reference.

## Properties

Name | Type | Description | Notes
------------ | ------------- | ------------- | -------------
**var_class** | **str** |  | 
**requested_line_range** | [**AnnotationLineRange**](AnnotationLineRange.md) |  | 
**target_reference_id** | **UUID** |  | 
**path** | **str** |  | 
**reference_type_filter** | [**List[ReferenceType]**](ReferenceType.md) |  | 
**max_references** | **int** |  | 
**include_line_text** | **bool** |  | 
**lines** | [**List[AnnotationLine]**](AnnotationLine.md) |  | 
**boundaries** | [**List[AnnotationBoundary]**](AnnotationBoundary.md) |  | 
**spans** | [**List[AnnotationSpan]**](AnnotationSpan.md) |  | 
**source_rows** | [**List[AnnotationSourceRow]**](AnnotationSourceRow.md) |  | 
**source_references** | [**List[AnnotationSourceReference]**](AnnotationSourceReference.md) |  | 

## Example

```python
from grace_generated_openapi_probe.models.branch_annotation_api_dto import BranchAnnotationApiDto

# TODO update the JSON string below
json = "{}"
# create an instance of BranchAnnotationApiDto from a JSON string
branch_annotation_api_dto_instance = BranchAnnotationApiDto.from_json(json)
# print the JSON string representation of the object
print(BranchAnnotationApiDto.to_json())

# convert the object into a dict
branch_annotation_api_dto_dict = branch_annotation_api_dto_instance.to_dict()
# create an instance of BranchAnnotationApiDto from a dict
branch_annotation_api_dto_from_dict = BranchAnnotationApiDto.from_dict(branch_annotation_api_dto_dict)
```
[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


