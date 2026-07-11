# BranchApiDto

## Properties

Name | Type | Description | Notes
------------ | ------------- | ------------- | -------------
**class** | **String** |  | 
**branch_id** | **uuid::Uuid** |  | 
**branch_name** | **String** |  | 
**parent_branch_id** | **uuid::Uuid** |  | 
**owner_id** | **uuid::Uuid** |  | 
**organization_id** | **uuid::Uuid** |  | 
**repository_id** | **uuid::Uuid** |  | 
**based_on** | [**models::ReferenceApiDto**](ReferenceApiDto.md) |  | 
**user_id** | **String** |  | 
**assign_enabled** | **bool** |  | 
**promotion_enabled** | **bool** |  | 
**commit_enabled** | **bool** |  | 
**checkpoint_enabled** | **bool** |  | 
**save_enabled** | **bool** |  | 
**tag_enabled** | **bool** |  | 
**external_enabled** | **bool** |  | 
**auto_rebase_enabled** | **bool** |  | 
**promotion_mode** | **String** |  | 
**latest_reference** | [**models::ReferenceApiDto**](ReferenceApiDto.md) |  | 
**latest_promotion** | [**models::ReferenceApiDto**](ReferenceApiDto.md) |  | 
**latest_commit** | [**models::ReferenceApiDto**](ReferenceApiDto.md) |  | 
**latest_checkpoint** | [**models::ReferenceApiDto**](ReferenceApiDto.md) |  | 
**latest_save** | [**models::ReferenceApiDto**](ReferenceApiDto.md) |  | 
**should_recompute_latest_references** | **bool** |  | 
**created_at** | **chrono::DateTime<chrono::FixedOffset>** |  | 
**updated_at** | Option<**chrono::DateTime<chrono::FixedOffset>**> |  | [optional]
**deleted_at** | Option<**chrono::DateTime<chrono::FixedOffset>**> |  | [optional]
**delete_reason** | **String** |  | 

[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


