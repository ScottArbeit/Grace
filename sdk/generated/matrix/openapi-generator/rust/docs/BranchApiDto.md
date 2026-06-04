# BranchApiDto

## Properties

Name | Type | Description | Notes
------------ | ------------- | ------------- | -------------
**class** | Option<**String**> |  | [optional]
**branch_id** | Option<**uuid::Uuid**> |  | [optional]
**branch_name** | Option<**String**> |  | [optional]
**parent_branch_id** | Option<**uuid::Uuid**> |  | [optional]
**owner_id** | Option<**uuid::Uuid**> |  | [optional]
**organization_id** | Option<**uuid::Uuid**> |  | [optional]
**repository_id** | Option<**uuid::Uuid**> |  | [optional]
**based_on** | Option<[**models::ReferenceApiDto**](ReferenceApiDto.md)> |  | [optional]
**user_id** | Option<**String**> |  | [optional]
**assign_enabled** | Option<**bool**> |  | [optional]
**promotion_enabled** | Option<**bool**> |  | [optional]
**commit_enabled** | Option<**bool**> |  | [optional]
**checkpoint_enabled** | Option<**bool**> |  | [optional]
**save_enabled** | Option<**bool**> |  | [optional]
**tag_enabled** | Option<**bool**> |  | [optional]
**external_enabled** | Option<**bool**> |  | [optional]
**auto_rebase_enabled** | Option<**bool**> |  | [optional]
**promotion_mode** | Option<**String**> |  | [optional]
**latest_reference** | Option<[**models::ReferenceApiDto**](ReferenceApiDto.md)> |  | [optional]
**latest_promotion** | Option<[**models::ReferenceApiDto**](ReferenceApiDto.md)> |  | [optional]
**latest_commit** | Option<[**models::ReferenceApiDto**](ReferenceApiDto.md)> |  | [optional]
**latest_checkpoint** | Option<[**models::ReferenceApiDto**](ReferenceApiDto.md)> |  | [optional]
**latest_save** | Option<[**models::ReferenceApiDto**](ReferenceApiDto.md)> |  | [optional]
**should_recompute_latest_references** | Option<**bool**> |  | [optional]
**created_at** | Option<**chrono::DateTime<chrono::FixedOffset>**> |  | [optional]
**updated_at** | Option<**chrono::DateTime<chrono::FixedOffset>**> |  | [optional]
**deleted_at** | Option<**chrono::DateTime<chrono::FixedOffset>**> |  | [optional]
**delete_reason** | Option<**String**> |  | [optional]

[[Back to Model list]](../README.md#documentation-for-models) [[Back to API list]](../README.md#documentation-for-api-endpoints) [[Back to README]](../README.md)


