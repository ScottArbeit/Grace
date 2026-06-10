# BranchAnnotationApiDto

## Properties

Name | Type | Description | Notes
------------ | ------------- | ------------- | -------------
**class** | **String** |  | 
**requested_line_range** | [**models::AnnotationLineRange**](AnnotationLineRange.md) |  | 
**target_reference_id** | **uuid::Uuid** |  | 
**path** | **String** |  | 
**reference_type_filter** | [**Vec<models::ReferenceType>**](ReferenceType.md) |  | 
**max_references** | **i32** |  | 
**include_line_text** | **bool** |  | 
**lines** | [**Vec<models::AnnotationLine>**](AnnotationLine.md) |  | 
**boundaries** | [**Vec<models::AnnotationBoundary>**](AnnotationBoundary.md) |  | 
**spans** | [**Vec<models::AnnotationSpan>**](AnnotationSpan.md) |  | 
**source_rows** | [**Vec<models::AnnotationSourceRow>**](AnnotationSourceRow.md) |  | 
**source_references** | [**Vec<models::AnnotationSourceReference>**](AnnotationSourceReference.md) |  | 

[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


