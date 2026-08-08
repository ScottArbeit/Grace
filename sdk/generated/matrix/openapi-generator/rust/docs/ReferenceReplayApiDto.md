# ReferenceReplayApiDto

## Properties

Name | Type | Description | Notes
------------ | ------------- | ------------- | -------------
**repository_id** | **uuid::Uuid** |  | 
**branch_id** | **uuid::Uuid** |  | 
**events** | [**Vec<models::ReferenceReplayEventApiDto>**](ReferenceReplayEventApiDto.md) |  | 
**scanned_through_cursor** | **String** | Opaque cursor closing the complete response interval; clients must not interpret or modify it. | 

[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


